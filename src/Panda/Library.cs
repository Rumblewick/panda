using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Panda;

public sealed record LibraryItem(
	string Id, string Title, string Channel, int Duration, long Size,
	int Plays, DateTimeOffset? LastPlayed, DateTimeOffset Added)
{
	public string? Source { get; init; }
	public string Url => Source == Media.Upload ? "" : $"https://www.youtube.com/watch?v={Id}";
	public string? Thumbnail { get; init; }
}

public sealed record LibraryInfo(bool Enabled, long UsedBytes, long LimitBytes, int Count, IReadOnlyList<LibraryItem> Items);

/// <param name="Plays">Goes up with every play, so the panel knows the play counts changed.</param>
public sealed record LibrarySummary(bool Enabled, long UsedBytes, long LimitBytes, int Count, long Plays);

public sealed record UploadResult(Track? Track, Track? Duplicate);

/// <summary>
/// Songs saved on disk. Every requested song is downloaded in the background (the next ones in the
/// queue first), so songs start instantly and favourites never hit YouTube again. When the folder
/// grows over the limit, the least played songs are deleted first.
/// With saving turned off it still downloads the next song ahead of time and deletes it after use.
/// </summary>
public sealed class Library : IDisposable
{
	const int PrefetchWhenDisabled = 2;
	static readonly TimeSpan RetryFailedAfter = TimeSpan.FromMinutes(10);

	readonly PandaConfig config;
	readonly YouTube youtube;
	readonly ILogger<Library> log;
	readonly string dataDir;

	readonly object gate = new();
	Dictionary<string, Song> songs = new();
	Dictionary<string, Stat> stats = new();
	// Per bot: the songs coming up and the one playing. "wanted" merges the queues, each bot's
	// next song first, then the ones after.
	readonly Dictionary<string, List<Track>> wantedBy = new();
	readonly Dictionary<string, List<string>> queuedUploadsBy = new();
	readonly Dictionary<string, string> nowPlayingBy = new();
	List<Track> wanted = [];
	readonly HashSet<string> busy = []; // ids being downloaded right now
	// Failed downloads wait before the next try, so a broken song (or a YouTube bot check)
	// doesn't make the loop hammer YouTube.
	readonly Dictionary<string, DateTime> retryAfter = new();
	readonly SemaphoreSlim wake = new(0);
	// Loudness: songs waiting to be measured, ones that couldn't be (until restart), and the
	// measurements of prefetched files (with saving off they aren't in the index).
	readonly List<string> toMeasure = [];
	readonly HashSet<string> unmeasurable = [];
	readonly Dictionary<string, (double Lufs, double Peak)> prefetchLoudness = new();
	readonly SemaphoreSlim measureWake = new(0);
	readonly CancellationTokenSource stop = new();
	CancellationTokenSource? currentDownload;
	string? currentDownloadId;
	Timer? saveTimer;
	readonly object saveLock = new();

	public event Action? Changed;

	public Library(PandaConfig config, YouTube youtube, DataPaths paths, ILogger<Library> log)
	{
		this.config = config;
		this.youtube = youtube;
		this.log = log;
		dataDir = paths.Data;
		Directory.CreateDirectory(Dir);
		Directory.CreateDirectory(TempDir);
		Load();
		_ = Task.Run(DownloadLoop);
		_ = Task.Run(MeasureLoop);
		MeasureMissing();
	}

	public Track? FindUploadByHash(string hash)
	{
		Track? duplicate;
		bool migrated;
		lock (gate)
		{
			duplicate = FindUploadByHashLocked(hash, out migrated) is { } song ? ToTrack(song, "") : null;
		}
		if (migrated) SaveSoon();
		return duplicate;
	}

	public UploadResult AddUpload(string tempPath, Track track, string hash, string? coverTempPath = null)
	{
		var id = "upload-" + Guid.NewGuid().ToString("N");
		var coverFile = coverTempPath is { } path && File.Exists(path) ? path : null;
		var uploaded = track with
		{
			Id = id, Source = Media.Upload, Link = null,
			Art = coverFile is not null ? Media.UploadCoverUrl(id) : null,
		};
		var size = new FileInfo(tempPath).Length;
		lock (gate)
		{
			var duplicate = FindUploadByHashLocked(hash, out var migrated);
			if (duplicate is not null)
			{
				if (migrated) SaveSoon();
				return new(null, ToTrack(duplicate, ""));
			}
			if (!CanFitUploadLocked(size))
			{
				if (migrated) SaveSoon();
				return new(null, null);
			}
			File.Move(tempPath, SongPath(id));
			try
			{
				if (coverFile is not null) File.Move(coverFile, CoverPath(id));
				songs.Add(id, new Song
				{
					Id = id, Title = uploaded.Title, Channel = uploaded.Channel,
					Duration = uploaded.Duration, Size = size, Added = DateTimeOffset.UtcNow, Source = Media.Upload, Hash = hash,
				});
			}
			catch
			{
				TryDelete(SongPath(id));
				TryDelete(CoverPath(id));
				throw;
			}
			EvictLocked(id);
			QueueMeasureLocked(id);
		}
		SaveSoon();
		Changed?.Invoke();
		log.LogInformation("Saved uploaded audio {Title} ({Size:0.0} MB)", uploaded.Title, size / 1048576.0);
		return new(uploaded, null);
	}

	public bool Enabled => config.Library.Enabled;
	public string Folder => Dir;
	string Dir => config.Library.Path.Length > 0 ? config.Library.Path : Path.Combine(dataDir, "library");
	string TempDir => Path.Combine(Dir, ".tmp");
	string IndexPath => Path.Combine(Dir, "library.json");
	string SongPath(string id) => Path.Combine(Dir, id + ".audio");
	string CoverPath(string id) => Path.Combine(Dir, id + ".cover.jpg");
	string PrefetchPath(string id) => Path.Combine(TempDir, id + ".audio");
	long LimitBytes => config.Library.MaxSizeMb <= 0 ? long.MaxValue : config.Library.MaxSizeMb * 1024L * 1024;
	bool CanFitUploadLocked(long size)
	{
		var limit = LimitBytes;
		if (size > limit) return false;
		var used = UsedLocked();
		var needed = used > limit - size ? used - (limit - size) : 0;
		if (needed == 0) return true;

		var keep = KeepLocked();
		long reclaimable = 0;
		foreach (var song in songs.Values
			.Where(s => !keep.Contains(s.Id))
			.OrderBy(s => PlaysOf(s.Id))
			.ThenBy(s => stats.GetValueOrDefault(s.Id)?.LastPlayed ?? s.Added))
		{
			if (reclaimable >= needed) return true;
			reclaimable += song.Size;
		}
		return reclaimable >= needed;
	}

	Song? FindUploadByHashLocked(string hash, out bool migrated)
	{
		migrated = false;
		foreach (var song in songs.Values.Where(s => s.Source == Media.Upload))
		{
			if (song.Hash is null && File.Exists(SongPath(song.Id)))
			{
				song.Hash = HashFile(SongPath(song.Id));
				migrated = true;
			}
			if (string.Equals(song.Hash, hash, StringComparison.OrdinalIgnoreCase)) return song;
		}
		return null;
	}

	static string HashFile(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
		return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
	}

	static bool IsUploadId(string id) => id.StartsWith("upload-", StringComparison.Ordinal)
		&& id.Length == 39 && id[7..].All(char.IsAsciiHexDigit);

	public string NewUploadTempPath() => Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".upload");
	public string NewUploadCoverTempPath() => Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".jpg");

	// ---- lookups ----

	/// <summary>A local file for this song (saved or prefetched), or null.</summary>
	public string? GetFile(string id)
	{
		lock (gate)
		{
			if (songs.TryGetValue(id, out var song) && File.Exists(SongPath(id))
				&& (Media.IsVideoId(id) || song.Source == Media.Upload && IsUploadId(id))) return SongPath(id);
		}
		if (!Media.IsVideoId(id)) return null;
		var tmp = PrefetchPath(id);
		return File.Exists(tmp) ? tmp : null;
	}

	public Track? Find(string id, string requestedBy)
	{
		lock (gate)
			return songs.TryGetValue(id, out var s) ? ToTrack(s, requestedBy) : null;
	}

	public bool IsUploaded(string id)
	{
		lock (gate) return IsUploadId(id) && songs.TryGetValue(id, out var song)
			&& song.Source == Media.Upload && File.Exists(SongPath(id));
	}

	public string? GetUploadCoverPath(string id)
	{
		lock (gate) return IsUploadId(id) && songs.TryGetValue(id, out var song)
			&& song.Source == Media.Upload && File.Exists(CoverPath(id)) ? CoverPath(id) : null;
	}

	/// <summary>Best saved song whose title/channel contains every word of the query.</summary>
	public Track? Search(string query, string requestedBy)
	{
		var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (words.Length == 0 || string.Concat(words).Length < 3) return null;
		lock (gate)
		{
			return songs.Values
				.Where(s => { var text = Normalize(s.Title + " " + s.Channel); return words.All(text.Contains); })
				.OrderByDescending(s => PlaysOf(s.Id))
				.FirstOrDefault() is { } song ? ToTrack(song, requestedBy) : null;
		}
	}

	public LibraryInfo GetInfo(string? query = null, int max = 500)
	{
		lock (gate)
		{
			IEnumerable<Song> list = songs.Values;
			if (!string.IsNullOrWhiteSpace(query))
			{
				var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
				list = list.Where(s => { var t = Normalize(s.Title + " " + s.Channel); return words.All(t.Contains); });
			}
			var items = list
				.OrderByDescending(s => PlaysOf(s.Id))
				.ThenByDescending(s => stats.GetValueOrDefault(s.Id)?.LastPlayed ?? s.Added)
				.Take(max)
				.Select(ToItem)
				.ToList();
			return new LibraryInfo(Enabled, UsedLocked(), ShownLimit, songs.Count, items);
		}
	}

	/// <summary>Counts only, for the live panel updates (no sorting).</summary>
	public LibrarySummary GetSummary()
	{
		lock (gate) return new(Enabled, UsedLocked(), ShownLimit, songs.Count, playsSeen);
	}

	long ShownLimit => config.Library.MaxSizeMb <= 0 ? 0 : LimitBytes;

	LibraryItem ToItem(Song s)
	{
		var st = stats.GetValueOrDefault(s.Id);
		return new(s.Id, s.Title, s.Channel, s.Duration, s.Size, st?.Plays ?? 0, st?.LastPlayed, s.Added)
		{
			Source = s.Source,
			Thumbnail = s.Source == Media.Upload
				? File.Exists(CoverPath(s.Id)) ? Media.UploadCoverUrl(s.Id) : null
				: $"https://i.ytimg.com/vi/{s.Id}/hqdefault.jpg",
		};
	}

	Track ToTrack(Song song, string requestedBy) => song.ToTrack(requestedBy) with
	{
		Art = song.Source == Media.Upload && File.Exists(CoverPath(song.Id)) ? Media.UploadCoverUrl(song.Id) : null,
	};

	int PlaysOf(string id) => stats.GetValueOrDefault(id)?.Plays ?? 0;
	long playsSeen;
	long UsedLocked() => songs.Values.Sum(s => s.Size);

	// ---- play statistics ----

	public void MarkPlayed(string bot, Track t)
	{
		lock (gate)
		{
			nowPlayingBy[bot] = t.Id;
			if (!stats.TryGetValue(t.Id, out var st)) stats[t.Id] = st = new Stat();
			st.Plays++;
			st.LastPlayed = DateTimeOffset.UtcNow;
			playsSeen++;
		}
		SaveSoon();
	}

	/// <summary>A song picked up again after a restart: kept like any playing song, not counted again.</summary>
	public void Resumed(string bot, Track t)
	{
		lock (gate) nowPlayingBy[bot] = t.Id;
		Signal();
	}

	public void ClearNowPlaying(string bot)
	{
		lock (gate) nowPlayingBy.Remove(bot);
		Signal();
	}

	/// <summary>A bot was removed: its queue and song no longer count.</summary>
	public void Forget(string bot)
	{
		lock (gate)
		{
			nowPlayingBy.Remove(bot);
			wantedBy.Remove(bot);
			queuedUploadsBy.Remove(bot);
			MergeWantedLocked();
		}
		Signal();
	}

	public bool Delete(string id)
	{
		if (!Media.IsVideoId(id) && !IsUploadId(id)) return false;
		lock (gate)
		{
			TryDelete(PrefetchPath(id));
			if (!songs.Remove(id)) return false;
			TryDelete(SongPath(id));
			TryDelete(CoverPath(id));
		}
		SaveSoon();
		Changed?.Invoke();
		return true;
	}

	/// <summary>Called after the settings changed: applies a new limit or turns saving on/off.</summary>
	public void SettingsChanged()
	{
		lock (gate) EvictLocked();
		SaveSoon();
		Signal();
		MeasureMissing();
		Changed?.Invoke();
	}

	// ---- loudness ----

	/// <summary>The song's measured integrated loudness (LUFS) and peak (dBFS), or null when it wasn't
	/// measured yet (<see cref="Leveler"/> then listens while it plays).</summary>
	public (double Lufs, double Peak)? LoudnessOf(string id)
	{
		lock (gate)
		{
			if (songs.TryGetValue(id, out var s) && s.Loudness is { } l) return (l, s.Peak ?? 0);
			return prefetchLoudness.TryGetValue(id, out var m) ? m : null;
		}
	}

	/// <summary>Queues every saved song that has no loudness yet (older libraries, or normalizing just turned on).</summary>
	void MeasureMissing()
	{
		if (!config.Audio.Normalize) return;
		lock (gate)
			foreach (var s in songs.Values.Where(s => s.Loudness is null).OrderByDescending(s => PlaysOf(s.Id)))
				QueueMeasureLocked(s.Id);
	}

	void QueueMeasureLocked(string id)
	{
		if (!config.Audio.Normalize || unmeasurable.Contains(id) || toMeasure.Contains(id)) return;
		toMeasure.Add(id);
		if (measureWake.CurrentCount == 0) measureWake.Release();
	}

	// One song at a time at low priority; a few hundred times faster than real time.
	async Task MeasureLoop()
	{
		var ct = stop.Token;
		while (!ct.IsCancellationRequested)
		{
			try { await measureWake.WaitAsync(ct); }
			catch (OperationCanceledException) { return; }
			while (!ct.IsCancellationRequested)
			{
				string id;
				lock (gate)
				{
					if (toMeasure.Count == 0) break;
					id = toMeasure[0];
					toMeasure.RemoveAt(0);
				}
				if (GetFile(id) is not { } file) continue;
				(double Lufs, double Peak)? m = null;
				try { m = await youtube.MeasureAsync(file, ct); }
				catch (OperationCanceledException) { return; }
				catch (Exception ex) { log.LogInformation("Could not measure {Id}: {Error}", id, ex.Message); }
				lock (gate)
				{
					if (m is null) unmeasurable.Add(id);
					else if (songs.TryGetValue(id, out var s)) { s.Loudness = Math.Round(m.Value.Lufs, 1); s.Peak = Math.Round(m.Value.Peak, 1); }
					else prefetchLoudness[id] = m.Value;
				}
				if (m != null) SaveSoon();
			}
		}
	}

	// ---- downloads ----

	/// <summary>A bot's queue changed: download what will be played (all of it when saving is on).</summary>
	public void Want(string bot, IReadOnlyList<Track> queue)
	{
		lock (gate)
		{
			// Only YouTube songs are saved; songs from elsewhere are played as they come.
			var list = queue.Where(t => !t.IsLive && t.IsYouTube);
			wantedBy[bot] = (Enabled ? list : list.Take(PrefetchWhenDisabled)).ToList();
			queuedUploadsBy[bot] = queue.Where(t => t.Source == Media.Upload && !t.IsLive).Select(t => t.Id).ToList();
			MergeWantedLocked();
			// A prefetch nobody needs any more is cancelled when songs aren't being saved.
			if (!Enabled && currentDownloadId != null && wanted.All(t => t.Id != currentDownloadId))
				currentDownload?.Cancel();
		}
		Signal();
	}

	void MergeWantedLocked()
	{
		var lists = wantedBy.Values.ToList();
		var merged = new List<Track>();
		for (int i = 0; lists.Any(l => i < l.Count); i++)
			foreach (var l in lists)
				if (i < l.Count) merged.Add(l[i]);
		wanted = merged;
	}

	HashSet<string> KeepLocked()
	{
		var keep = wanted.Select(t => t.Id).ToHashSet();
		foreach (var uploads in queuedUploadsBy.Values) keep.UnionWith(uploads);
		keep.UnionWith(nowPlayingBy.Values);
		return keep;
	}

	void Signal() { if (wake.CurrentCount == 0) wake.Release(); }

	async Task DownloadLoop()
	{
		var ct = stop.Token;
		while (!ct.IsCancellationRequested)
		{
			try { await wake.WaitAsync(TimeSpan.FromSeconds(30), ct); }
			catch (OperationCanceledException) { return; }

			CleanupPrefetched();
			while (!ct.IsCancellationRequested && NextToDownload() is { } track)
				await DownloadAsync(track, ct);
		}
	}

	Track? NextToDownload()
	{
		lock (gate)
		{
			var max = config.YouTube.MaxDurationMinutes;
			var now = DateTime.UtcNow;
			return wanted.FirstOrDefault(t =>
				!busy.Contains(t.Id)
				&& !(max > 0 && t.Duration > max * 60)
				&& !(retryAfter.TryGetValue(t.Id, out var until) && until > now)
				&& t.IsYouTube
				&& !songs.ContainsKey(t.Id)
				&& !File.Exists(PrefetchPath(t.Id)));
		}
	}

	async Task DownloadAsync(Track track, CancellationToken stopToken)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
		lock (gate)
		{
			busy.Add(track.Id);
			currentDownload = cts;
			currentDownloadId = track.Id;
		}
		// Not ".part": yt-dlp treats that suffix specially and drops it.
		var part = Path.Combine(TempDir, track.Id + ".dl");
		var failed = false;
		try
		{
			if (await youtube.DownloadAsync(track, part, cts.Token) && File.Exists(part))
				Store(track, part);
			else
			{
				failed = true;
				TryDelete(part);
			}
		}
		catch (OperationCanceledException) { TryDelete(part); }
		catch (Exception ex)
		{
			failed = true;
			log.LogWarning("Download of {Id} failed: {Error}", track.Id, ex.Message);
			TryDelete(part);
		}
		finally
		{
			lock (gate)
			{
				busy.Remove(track.Id);
				currentDownload = null;
				currentDownloadId = null;
				foreach (var id in retryAfter.Where(r => r.Value < DateTime.UtcNow).Select(r => r.Key).ToList())
					retryAfter.Remove(id);
				if (failed) retryAfter[track.Id] = DateTime.UtcNow + RetryFailedAfter;
			}
		}
	}

	/// <summary>
	/// While a song is streamed straight from YouTube, the bytes are also written here so it ends up
	/// in the library without a second download. Returns null when that isn't wanted.
	/// </summary>
	public string? BeginTee(Track track)
	{
		if (track.IsLive || !track.IsYouTube || !Enabled) return null;
		lock (gate)
		{
			if (songs.ContainsKey(track.Id) || !busy.Add(track.Id)) return null;
		}
		return Path.Combine(TempDir, track.Id + ".tee");
	}

	public void EndTee(Track track, string path, bool complete)
	{
		try
		{
			if (complete && File.Exists(path) && new FileInfo(path).Length > 0) Store(track, path);
			else TryDelete(path);
		}
		catch (Exception ex)
		{
			log.LogWarning("Could not save {Id}: {Error}", track.Id, ex.Message);
			TryDelete(path);
		}
		finally
		{
			lock (gate) busy.Remove(track.Id);
			Signal();
		}
	}

	void Store(Track track, string file)
	{
		var size = new FileInfo(file).Length;
		lock (gate)
		{
			if (Enabled)
			{
				File.Move(file, SongPath(track.Id), overwrite: true);
				songs[track.Id] = new Song
				{
					Id = track.Id, Title = track.Title, Channel = track.Channel,
					Duration = track.Duration, Size = size, Added = DateTimeOffset.UtcNow, Source = track.Source,
				};
				EvictLocked();
				log.LogInformation("Saved {Title} ({Size:0.0} MB)", track.Title, size / 1048576.0);
			}
			else
			{
				File.Move(file, PrefetchPath(track.Id), overwrite: true);
				log.LogInformation("Ready ahead of time: {Title}", track.Title);
			}
			QueueMeasureLocked(track.Id);
		}
		SaveSoon();
		Changed?.Invoke();
	}

	// Deletes the least played songs (oldest first among equals) until the folder fits the limit.
	// Songs that are playing or waiting in the queue are kept.
	void EvictLocked()
		=> EvictLocked(null);

	void EvictLocked(string? keepId)
	{
		var limit = LimitBytes;
		var used = UsedLocked();
		if (used <= limit) return;
		var keep = KeepLocked();
		if (keepId != null) keep.Add(keepId);
		var victims = songs.Values
			.Where(s => !keep.Contains(s.Id))
			.OrderBy(s => PlaysOf(s.Id))
			.ThenBy(s => stats.GetValueOrDefault(s.Id)?.LastPlayed ?? s.Added)
			.ToList();
		foreach (var v in victims)
		{
			if (used <= limit) break;
			songs.Remove(v.Id);
			TryDelete(SongPath(v.Id));
			TryDelete(CoverPath(v.Id));
			used -= v.Size;
			log.LogInformation("Library full: removed {Title} ({Plays} plays)", v.Title, PlaysOf(v.Id));
		}
	}

	// Prefetched files (saving off) are only kept while they're still coming up or playing.
	void CleanupPrefetched()
	{
		HashSet<string> keep;
		lock (gate)
		{
			keep = KeepLocked();
			keep.UnionWith(busy);
		}
		foreach (var f in Directory.EnumerateFiles(TempDir, "*.audio"))
			if (!keep.Contains(Path.GetFileNameWithoutExtension(f)))
				TryDelete(f);
		lock (gate)
			foreach (var id in prefetchLoudness.Keys.Where(id => !keep.Contains(id)).ToList())
				prefetchLoudness.Remove(id);
	}

	// ---- persistence ----

	void Load()
	{
		foreach (var f in Directory.EnumerateFiles(TempDir)) TryDelete(f);
		var indexRead = true;
		if (File.Exists(IndexPath))
		{
			try
			{
				var idx = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(IndexPath), JsonOpts);
				if (idx != null)
				{
					songs = idx.Songs.Where(s => Media.IsVideoId(s.Id) || s.Source == Media.Upload && IsUploadId(s.Id))
						.DistinctBy(s => s.Id).ToDictionary(s => s.Id);
					stats = idx.Stats;
				}
			}
			catch (Exception ex)
			{
				// Keep the broken index and the songs: deleting "unknown" files now would wipe the library.
				indexRead = false;
				log.LogWarning("Could not read {Path}: {Error}. A copy is kept as library.json.bad", IndexPath, ex.Message);
				try { File.Copy(IndexPath, IndexPath + ".bad", overwrite: true); } catch { }
			}
		}
		// Forget entries whose file is gone and files nobody knows about.
		foreach (var id in songs.Keys.Where(id => !File.Exists(SongPath(id))).ToList())
			songs.Remove(id);
		if (indexRead)
			foreach (var f in Directory.EnumerateFiles(Dir, "*.audio"))
				if (!songs.ContainsKey(Path.GetFileNameWithoutExtension(f)))
					TryDelete(f);
		foreach (var f in Directory.EnumerateFiles(Dir, "upload-*.cover.jpg"))
		{
			var id = Path.GetFileName(f)[..^".cover.jpg".Length];
			if (!songs.ContainsKey(id)) TryDelete(f);
		}
	}

	void SaveSoon()
	{
		lock (gate)
		{
			saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
			saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
		}
	}

	void Save()
	{
		lock (saveLock) // the timer and Dispose may save at the same time
		{
			try
			{
				string json;
				lock (gate)
					json = JsonSerializer.Serialize(new IndexFile { Songs = songs.Values.ToList(), Stats = stats }, JsonOpts);
				SafeFile.Write(IndexPath, json);
			}
			catch (Exception ex)
			{
				log.LogWarning("Could not save the library index: {Error}", ex.Message);
			}
		}
	}

	static void TryDelete(string path)
	{
		try { File.Delete(path); } catch { }
	}

	// The app runs in invariant globalization mode, where string.Normalize can't strip accents,
	// so the common accented letters are folded by hand.
	const string Accented = "şçğüöıiâäàáãåêëèéîïìíôòóõûùúñßøæœ";
	const string Plain    = "scguoiiaaaaaaeeeeiiiioooouuunsoao";

	/// <summary>Lower case without accents, so "simarik" finds "Şımarık".</summary>
	public static string Normalize(string s)
	{
		var lower = s.ToLowerInvariant();
		var sb = new StringBuilder(lower.Length);
		foreach (var c in lower)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
			var i = Accented.IndexOf(c);
			if (i >= 0) sb.Append(Plain[i]);
			else sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
		}
		return sb.ToString();
	}

	public void Dispose()
	{
		stop.Cancel();
		saveTimer?.Dispose();
		Save();
	}

	static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	sealed class IndexFile
	{
		public List<Song> Songs { get; set; } = [];
		public Dictionary<string, Stat> Stats { get; set; } = new();
	}

	sealed class Song
	{
		public string Id { get; set; } = "";
		public string? Source { get; set; }
		public string? Hash { get; set; }
		public string Title { get; set; } = "";
		public string Channel { get; set; } = "";
		public int Duration { get; set; }
		public long Size { get; set; }
		public DateTimeOffset Added { get; set; }
		/// <summary>Integrated loudness (LUFS) and sample peak (dBFS); null until measured.</summary>
		public double? Loudness { get; set; }
		public double? Peak { get; set; }
		public Track ToTrack(string requestedBy) => new(Id, Title, Channel, Duration, requestedBy) { Source = Source };
	}

	sealed class Stat
	{
		public int Plays { get; set; }
		public DateTimeOffset? LastPlayed { get; set; }
	}
}
