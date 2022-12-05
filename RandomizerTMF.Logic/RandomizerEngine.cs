﻿using GBX.NET;
using GBX.NET.Engines.Game;
using Microsoft.Extensions.Logging;
using RandomizerTMF.Logic.Exceptions;
using RandomizerTMF.Logic.TypeConverters;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using TmEssentials;
using YamlDotNet.Serialization;
using static GBX.NET.Engines.Game.CGamePlayerScore;
using static GBX.NET.Engines.Hms.CHmsLightMapCache;

namespace RandomizerTMF.Logic;

public static class RandomizerEngine
{
    private static bool isActualSkipCancellation;
    private static string? userDataDirectoryPath;
    private static bool hasAutosavesScanned;
    
    private static ISerializer YamlSerializer { get; } = new SerializerBuilder()
        .WithTypeConverter(new DateOnlyConverter())
        .WithTypeConverter(new DateTimeOffsetConverter())
        .WithTypeConverter(new TimeInt32Converter())
        .Build();

    private static IDeserializer YamlDeserializer { get; } = new DeserializerBuilder()
        .WithTypeConverter(new DateOnlyConverter())
        .WithTypeConverter(new DateTimeOffsetConverter())
        .WithTypeConverter(new TimeInt32Converter())
        .Build();

    public static RandomizerConfig Config { get; }

    public static string? TmForeverExeFilePath { get; set; }
    public static string? TmUnlimiterExeFilePath { get; set; }

    /// <summary>
    /// General directory of the user data. It also sets the <see cref="AutosavesDirectoryPath"/>, <see cref="DownloadedDirectoryPath"/>, and <see cref="AutosaveWatcher"/> path with it.
    /// </summary>
    public static string? UserDataDirectoryPath
    {
        get => userDataDirectoryPath;
        set
        {
            userDataDirectoryPath = value;

            AutosavesDirectoryPath = userDataDirectoryPath is null ? null : Path.Combine(userDataDirectoryPath, "Tracks", "Replays", "Autosaves");
            DownloadedDirectoryPath = userDataDirectoryPath is null ? null : Path.Combine(userDataDirectoryPath, "Tracks", "Challenges", "Downloaded", string.IsNullOrWhiteSpace(Config.DownloadedMapsDirectory) ? Constants.DownloadedMapsDirectory : Config.DownloadedMapsDirectory);

            AutosaveWatcher.Path = AutosavesDirectoryPath ?? "";
        }
    }

    public static string? AutosavesDirectoryPath { get; private set; }
    public static string? DownloadedDirectoryPath { get; private set; }
    public static string SessionsDirectoryPath => Constants.Sessions;

    public static Task? CurrentSession { get; private set; }
    public static CancellationTokenSource? CurrentSessionTokenSource { get; private set; }
    public static CancellationTokenSource? SkipTokenSource { get; private set; }
    public static SessionMap? CurrentSessionMap { get; private set; }
    public static string? CurrentSessionMapSavePath { get; private set; }
    public static Stopwatch? CurrentSessionWatch { get; private set; }

    public static SessionData? CurrentSessionData { get; private set; }
    public static string? CurrentSessionDataDirectoryPath => CurrentSessionData is null ? null : Path.Combine(SessionsDirectoryPath, CurrentSessionData.StartedAtText);

    // This "trilogy" handles the storage of played maps. If the player didn't receive at least gold and didn't skip it, it is not counted in the progress.
    // It may (?) be better to wrap the CGameCtnChallenge into "CompletedMap" and have status of it being "gold", "author", or "skipped", and better handle that to make it script-friendly.
    public static Dictionary<string, SessionMap> CurrentSessionGoldMaps { get; } = new();
    public static Dictionary<string, SessionMap> CurrentSessionAuthorMaps { get; } = new();
    public static Dictionary<string, SessionMap> CurrentSessionSkippedMaps { get; } = new();

    public static bool HasSessionRunning => CurrentSession is not null;

    /// <summary>
    /// If the map UIDs of the autosaves have been fully stored to the <see cref="Autosaves"/> and <see cref="AutosavePaths"/> dictionaries.
    /// This property is required to be true in order to start a new session. It also handles the state of <see cref="AutosaveWatcher"/>, to catch other autosaves that might be created while the program is running.
    /// </summary>
    public static bool HasAutosavesScanned
    {
        get => hasAutosavesScanned;
        private set
        {
            hasAutosavesScanned = value;
            AutosaveWatcher.EnableRaisingEvents = value;
        }
    }

    public static ConcurrentDictionary<string, CGameCtnReplayRecord> Autosaves { get; } = new();
    public static ConcurrentDictionary<string, string> AutosavePaths { get; } = new();
    public static ConcurrentDictionary<string, AutosaveDetails> AutosaveDetails { get; } = new();

    public static FileSystemWatcher AutosaveWatcher { get; }

    public static event Action? MapStarted;
    public static event Action? MapEnded;
    public static event Action? MapSkip;
    public static event Action<string> Status;
    public static event Action? MedalUpdate;

    public static ILogger Logger { get; private set; }
    public static StreamWriter LogWriter { get; private set; }
    public static StreamWriter? CurrentSessionLogWriter { get; private set; }
    public static bool SessionEnding { get; private set; }

    private static int RequestAttempt { get; set; }
    private static int RequestMaxAttempts { get; } = 10;

    static RandomizerEngine()
    {
        LogWriter = new StreamWriter("RandomizerTMF.log", append: true)
        {
            AutoFlush = true
        };

        Logger = new LoggerToFile(LogWriter);
        
        Logger.LogInformation("Starting Randomizer Engine...");
        Logger.LogInformation("Predefining LZO algorithm...");

        Directory.CreateDirectory(SessionsDirectoryPath);

        GBX.NET.Lzo.SetLzo(typeof(GBX.NET.LZO.MiniLZO));

        Logger.LogInformation("Loading config...");

        Config = GetOrCreateConfig();

        Logger.LogInformation("Preparing general events...");

        AutosaveWatcher = new FileSystemWatcher
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            Filter = "*.Replay.gbx"
        };

        AutosaveWatcher.Changed += AutosaveCreatedOrChanged;

        Status += RandomizerEngineStatus;
    }

    private static void RandomizerEngineStatus(string status)
    {
        // Status kind of logging
        // Unlike regular logs, these are shown to the user in a module, while also written to the log file in its own way
        Logger.LogInformation("STATUS: {status}", status);
    }
    
    private static DateTime lastAutosaveUpdate = DateTime.MinValue;

    private static void AutosaveCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        // Hack to fix the issue of this event sometimes running twice
        var lastWriteTime = File.GetLastWriteTime(e.FullPath);

        if (lastWriteTime == lastAutosaveUpdate)
        {
            return;
        }
        
        lastAutosaveUpdate = lastWriteTime;
        //

        CGameCtnReplayRecord replay;

        try
        {
            // Any kind of autosaves update section
            Logger.LogInformation("Analyzing a new file {autosavePath} in autosaves folder...", e.FullPath);

            if (GameBox.ParseNode(e.FullPath) is not CGameCtnReplayRecord r)
            {
                Logger.LogWarning("Found file {file} that is not a replay.", e.FullPath);
                return;
            }

            if (r.MapInfo is null)
            {
                Logger.LogWarning("Found replay {file} that has no map info.", e.FullPath);
                return;
            }

            Autosaves.TryAdd(r.MapInfo.Id, r);
            AutosavePaths.TryAdd(r.MapInfo.Id, Path.GetFileName(e.FullPath));

            replay = r;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error while analyzing a new file {autosavePath} in autosaves folder.", e.FullPath);
            return;
        }

        try
        {
            // Current session map autosave update section

            if (CurrentSessionMap is null)
            {
                Logger.LogWarning("Found autosave {autosavePath} for map {mapUid} while no session is running.", e.FullPath, replay.MapInfo.Id);
                return;
            }

            if (CurrentSessionMap.MapInfo != replay.MapInfo)
            {
                Logger.LogWarning("Found autosave {autosavePath} for map {mapUid} while the current session map is {currentSessionMapUid}.", e.FullPath, replay.MapInfo.Id, CurrentSessionMap.MapInfo.Id);
                return;
            }
            
            UpdateSessionDataFromAutosave(e.FullPath, CurrentSessionMap, replay);

            Status("Checking the autosave...");

            // New autosave from the current map, save it into session for progression reasons

            // The following part has a scriptable potential
            // There are different medal rules for each gamemode (and where to look for validating)
            // So that's why the code looks like this for the time being

            if (CurrentSessionMap.ChallengeParameters?.AuthorTime is null)
            {
                Logger.LogWarning("Found autosave {autosavePath} for map {mapName} ({mapUid}) that has no author time.",
                    e.FullPath,
                    TextFormatter.Deformat(CurrentSessionMap.Map.MapName).Trim(),
                    replay.MapInfo.Id);

                SkipTokenSource?.Cancel();
            }
            else
            {
                if ((CurrentSessionMap.Mode is CGameCtnChallenge.PlayMode.Race or CGameCtnChallenge.PlayMode.Puzzle && replay.Time <= CurrentSessionMap.ChallengeParameters.AuthorTime)
                 || (CurrentSessionMap.Mode is CGameCtnChallenge.PlayMode.Platform && replay.GetGhosts().First().Respawns <= CurrentSessionMap.ChallengeParameters.AuthorScore && replay.Time <= CurrentSessionMap.ChallengeParameters.AuthorTime)
                 || (CurrentSessionMap.Mode is CGameCtnChallenge.PlayMode.Stunts && replay.GetGhosts().First().StuntScore >= CurrentSessionMap.ChallengeParameters.AuthorScore))
                {
                    AuthorMedalReceived(CurrentSessionMap);

                    SkipTokenSource?.Cancel();
                }
                else if ((CurrentSessionMap.Mode is CGameCtnChallenge.PlayMode.Race or CGameCtnChallenge.PlayMode.Puzzle && replay.Time <= CurrentSessionMap.ChallengeParameters.GoldTime)
                      || (CurrentSessionMap.Mode is CGameCtnChallenge.PlayMode.Platform && replay.GetGhosts().First().Respawns <= CurrentSessionMap.ChallengeParameters.GoldTime.GetValueOrDefault().TotalMilliseconds)
                      || (CurrentSessionMap.Mode is CGameCtnChallenge.PlayMode.Stunts && replay.GetGhosts().First().StuntScore >= CurrentSessionMap.ChallengeParameters.GoldTime.GetValueOrDefault().TotalMilliseconds))
                {
                    GoldMedalReceived(CurrentSessionMap);
                }
            }

            Status("Playing the map...");
        }
        catch (Exception ex)
        {
            Status("Error when checking the autosave...");
            Logger.LogError(ex, "Error when checking the autosave {autosavePath}.", e.FullPath);
        }
    }

    private static void UpdateSessionDataFromAutosave(string fullPath, SessionMap map, CGameCtnReplayRecord replay)
    {
        if (CurrentSessionDataDirectoryPath is null)
        {
            return;
        }

        Status("Copying the autosave...");

        var score = map.Map.Mode switch
        {
            CGameCtnChallenge.PlayMode.Stunts => replay.GetGhosts().First().StuntScore + "_",
            CGameCtnChallenge.PlayMode.Platform => replay.GetGhosts().First().Respawns + "_",
            _ => ""
        } + replay.Time.ToTmString(useHundredths: true, useApostrophe: true);

        var mapName = TextFormatter.Deformat(map.Map.MapName).Trim();
        var replayFileName = $"{mapName}_{score}.Replay.Gbx";

        var replaysDir = Path.Combine(CurrentSessionDataDirectoryPath, "Replays");
        var replayFilePath = Path.Combine(replaysDir, replayFileName);

        Directory.CreateDirectory(replaysDir);
        File.Copy(fullPath, replayFilePath, overwrite: true);

        if (CurrentSessionWatch is null)
        {
            return;
        }
        
        CurrentSessionData?.Maps
            .FirstOrDefault(x => x.Uid == map.MapUid)?
            .Replays
            .Add(new()
            {
                FileName = replayFileName,
                Timestamp = CurrentSessionWatch.Elapsed
            });
        
        SaveSessionData();
    }

    private static void GoldMedalReceived(SessionMap map)
    {
        CurrentSessionGoldMaps.TryAdd(map.MapUid, map);
        map.LastChangeAt = CurrentSessionWatch?.Elapsed;
        SetMapResult(map, "GoldMedal");

        MedalUpdate?.Invoke();
    }

    private static void AuthorMedalReceived(SessionMap map)
    {
        CurrentSessionGoldMaps.Remove(map.MapUid);
        CurrentSessionAuthorMaps.TryAdd(map.MapUid, map);
        map.LastChangeAt = CurrentSessionWatch?.Elapsed;
        SetMapResult(map, "AuthorMedal");

        MedalUpdate?.Invoke();
    }

    private static void Skipped(SessionMap map)
    {
        // If the player didn't receive at least a gold medal, the skip is counted (author medal automatically skips the map)
        if (!CurrentSessionGoldMaps.ContainsKey(map.MapUid))
        {
            CurrentSessionSkippedMaps.TryAdd(map.MapUid, map);
            map.LastChangeAt = CurrentSessionWatch?.Elapsed;
            SetMapResult(map, "Skipped");
        }

        // In other words, if the player received at least a gold medal, the skip is forgiven

        // MapSkip event is thrown to update the UI
        MapSkip?.Invoke();
    }

    private static void SetMapResult(SessionMap map, string result)
    {
        var dataMap = CurrentSessionData?.Maps.FirstOrDefault(x => x.Uid == map.MapUid);

        if (dataMap is not null)
        {
            dataMap.Result = result;
            dataMap.LastTimestamp = map.LastChangeAt;
        }
        
        SaveSessionData();
    }

    /// <summary>
    /// This method should be ran only at the start of the randomizer engine.
    /// </summary>
    /// <returns></returns>
    private static RandomizerConfig GetOrCreateConfig()
    {
        var config = default(RandomizerConfig);

        if (File.Exists(Constants.ConfigYml))
        {
            Logger.LogInformation("Config file found, loading...");

            try
            {
                using var reader = new StreamReader(Constants.ConfigYml);
                config = YamlDeserializer.Deserialize<RandomizerConfig>(reader);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error while deserializing the config file ({configPath}).", Constants.ConfigYml);
            }
        }

        if (config is null)
        {
            Logger.LogInformation("Config file not found or is corrupted, creating a new one...");
            config = new RandomizerConfig();
        }

        SaveConfig(config);

        return config;
    }

    public static GameDirInspectResult UpdateGameDirectory(string gameDirectoryPath)
    {
        Config.GameDirectory = gameDirectoryPath;

        var nadeoIniFilePath = Path.Combine(gameDirectoryPath, Constants.NadeoIni);
        var tmForeverExeFilePath = Path.Combine(gameDirectoryPath, Constants.TmForeverExe);
        var tmUnlimiterExeFilePath = Path.Combine(gameDirectoryPath, Constants.TmInifinityExe);

        var nadeoIniException = default(Exception);
        var tmForeverExeException = default(Exception);
        var tmUnlimiterExeException = default(Exception);

        try
        {
            var nadeoIni = NadeoIni.Parse(nadeoIniFilePath);
            var newUserDataDirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), nadeoIni.UserSubDir);

            if (UserDataDirectoryPath != newUserDataDirectoryPath)
            {
                UserDataDirectoryPath = newUserDataDirectoryPath;

                ResetAutosaves();
            }
        }
        catch (Exception ex)
        {
            nadeoIniException = ex;
        }

        try
        {
            using var fs = File.OpenRead(tmForeverExeFilePath);
            TmForeverExeFilePath = tmForeverExeFilePath;
        }
        catch (Exception ex)
        {
            tmForeverExeException = ex;
        }

        try
        {
            using var fs = File.OpenRead(tmUnlimiterExeFilePath);
            TmUnlimiterExeFilePath = tmUnlimiterExeFilePath;
        }
        catch (Exception ex)
        {
            tmUnlimiterExeException = ex;
        }

        return new GameDirInspectResult(nadeoIniException, tmForeverExeException, tmUnlimiterExeException);
    }

    /// <summary>
    /// Cleans up the autosave storage and, most importantly, restricts the engine from functionalities that require the autosaves (<see cref="HasAutosavesScanned"/>).
    /// </summary>
    public static void ResetAutosaves()
    {
        Autosaves.Clear();
        AutosavePaths.Clear();
        AutosaveDetails.Clear();
        HasAutosavesScanned = false;
    }

    public static void SaveConfig()
    {
        SaveConfig(Config);
    }

    private static void SaveConfig(RandomizerConfig config)
    {
        Logger.LogInformation("Saving the config file...");
        File.WriteAllText(Constants.ConfigYml, YamlSerializer.Serialize(config));

        Logger.LogInformation("Config file saved.");
    }

    private static void SaveSessionData()
    {
        if (CurrentSessionData is null || CurrentSessionDataDirectoryPath is null)
        {
            return;
        }

        Logger.LogInformation("Saving the session data into file...");
        
        File.WriteAllText(Path.Combine(CurrentSessionDataDirectoryPath, Constants.SessionYml), YamlSerializer.Serialize(CurrentSessionData));

        Logger.LogInformation("Session data saved.");
    }

    /// <summary>
    /// Scans the autosaves, which is required before running the session, to avoid already played maps.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    /// <exception cref="Exception"></exception>
    public static bool ScanAutosaves()
    {
        if (AutosavesDirectoryPath is null)
        {
            throw new Exception("Cannot scan autosaves without a valid user data directory path.");
        }

        var anythingChanged = false;

        foreach (var file in Directory.EnumerateFiles(AutosavesDirectoryPath).AsParallel())
        {
            if (GameBox.ParseNodeHeader(file) is not CGameCtnReplayRecord replay || replay.MapInfo is null)
            {
                continue;
            }

            var mapUid = replay.MapInfo.Id;

            if (Autosaves.ContainsKey(mapUid))
            {
                continue;
            }

            // These two should probably merge under an AutosaveHeader class (different from AutosaveDetails).
            Autosaves.TryAdd(mapUid, replay);
            AutosavePaths.TryAdd(mapUid, Path.GetFileName(file));

            anythingChanged = true;
        }

        HasAutosavesScanned = true;

        return anythingChanged;
    }

    /// <summary>
    /// Scans autosave details (mostly the map inside the replay and getting its info) that are then used to display more detailed information about a list of maps. Only some replay properties are stored to value memory.
    /// </summary>
    /// <exception cref="Exception"></exception>
    public static void ScanDetailsFromAutosaves()
    {
        if (AutosavesDirectoryPath is null)
        {
            throw new Exception("Cannot update autosaves without a valid user data directory path.");
        }

        foreach (var autosave in Autosaves.Keys)
        {
            UpdateAutosaveDetail(autosave);
        }
    }

    private static void UpdateAutosaveDetail(string autosaveFileName)
    {
        var autosavePath = Path.Combine(AutosavesDirectoryPath!, AutosavePaths[autosaveFileName]); // Forgive because it's a private method

        if (GameBox.ParseNode(autosavePath) is not CGameCtnReplayRecord { Time: not null } replay)
        {
            return;
        }

        try
        {
            if (replay.Challenge is null)
            {
                return;
            }

            var mapName = TextFormatter.Deformat(replay.Challenge.MapName);
            var mapEnv = (string)replay.Challenge.Collection;
            var mapBronzeTime = replay.Challenge.TMObjective_BronzeTime ?? throw new Exception("Bronze time is null.");
            var mapSilverTime = replay.Challenge.TMObjective_SilverTime ?? throw new Exception("Silver time is null.");
            var mapGoldTime = replay.Challenge.TMObjective_GoldTime ?? throw new Exception("Gold time is null.");
            var mapAuthorTime = replay.Challenge.TMObjective_AuthorTime ?? throw new Exception("Author time is null.");
            var mapAuthorScore = replay.Challenge.AuthorScore ?? throw new Exception("AuthorScore is null.");
            var mapMode = replay.Challenge.Mode;
            var mapCarPure = replay.Challenge.PlayerModel?.Id;
            var mapCar = string.IsNullOrEmpty(mapCarPure) ? $"{mapEnv}Car" : mapCarPure;

            mapCar = mapCar switch
            {
                "AlpineCar" => "SnowCar",
                "American" or "SpeedCar" => "DesertCar",
                "Rally" => "RallyCar",
                "SportCar" => "IslandCar",
                _ => mapCar
            };

            var ghost = replay.GetGhosts().FirstOrDefault();

            AutosaveDetails[autosaveFileName] = new(
                replay.Time.Value,
                Score: ghost?.StuntScore,
                Respawns: ghost?.Respawns,
                mapName,
                mapEnv,
                mapCar,
                mapBronzeTime,
                mapSilverTime,
                mapGoldTime,
                mapAuthorTime,
                mapAuthorScore,
                mapMode);
        }
        catch
        {

        }
    }

    /// <summary>
    /// Starts the randomizer session by creating a new watch and <see cref="Task"/> for <see cref="CurrentSession"/> (+ <see cref="CurrentSessionTokenSource"/>) that will handle randomization on different thread from the UI thread.
    /// </summary>
    /// <returns></returns>
    public static Task StartSessionAsync()
    {
        if (Config.GameDirectory is null)
        {
            return Task.CompletedTask;
        }

        CurrentSessionWatch = new Stopwatch();
        CurrentSessionTokenSource = new CancellationTokenSource();
        CurrentSession = Task.Run(() => RunSessionAsync(CurrentSessionTokenSource.Token), CurrentSessionTokenSource.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Does the actual work during a running session. That this method ends means the session also ends.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private static async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        if (Config.GameDirectory is null)
        {
            throw new Exception("Game directory is null");
        }

        ValidateRules();

        InitializeSessionData();

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Randomizer TMF (beta)");

        try
        {
            while (true)
            {
                await PlayNewMapAsync(http, cancellationToken);
            }
        }
        catch (TaskCanceledException)
        {
            Status("Session ended.");
        }
        catch (InvalidSessionException)
        {
            Status("Session ended. No maps found.");
        }

        ClearCurrentSession();
    }

    private static void InitializeSessionData()
    {
        var startedAt = DateTimeOffset.Now;

        CurrentSessionData = new SessionData(startedAt);

        if (CurrentSessionDataDirectoryPath is null)
        {
            throw new UnreachableException("CurrentSessionDataDirectoryPath is null");
        }

        Directory.CreateDirectory(CurrentSessionDataDirectoryPath);

        CurrentSessionLogWriter = new StreamWriter(Path.Combine(CurrentSessionDataDirectoryPath, Constants.SessionLog))
        {
            AutoFlush = true
        };

        Logger = new LoggerToFile(CurrentSessionLogWriter, LogWriter);

        SaveSessionData();
    }

    /// <summary>
    /// Validates the session rules. This should be called right before the session start and after loading the modules.
    /// </summary>
    /// <exception cref="InvalidDataException"></exception>
    public static void ValidateRules()
    {
        if (Config.Rules.TimeLimit == TimeSpan.Zero)
        {
            throw new InvalidDataException("Time limit cannot be 0:00:00");
        }

        if (Config.Rules.TimeLimit > new TimeSpan(9, 59, 59))
        {
            throw new InvalidDataException("Time limit cannot be above 9:59:59");
        }
    }

    /// <summary>
    /// Does the cleanup of the session so that the new one can be instantiated without issues.
    /// </summary>
    private static void ClearCurrentSession()
    {
        StopTrackingCurrentSessionMap();

        CurrentSessionGoldMaps.Clear();
        CurrentSessionAuthorMaps.Clear();
        CurrentSessionSkippedMaps.Clear();

        CurrentSessionWatch?.Stop();
        CurrentSessionWatch = null;

        CurrentSession = null;
        CurrentSessionTokenSource = null;

        CurrentSessionData = null;

        CurrentSessionLogWriter?.Dispose();
        CurrentSessionLogWriter = null;

        Logger = new LoggerToFile(LogWriter);
    }

    /// <summary>
    /// Does the work behind individual maps played throughout the session. Network exceptions are handled within the method. Throws cancellation exception on session end (not the map end). It may get reworked though.
    /// </summary>
    /// <param name="http"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidSessionException"></exception>
    /// <exception cref="UnreachableException"></exception>
    private static async Task PlayNewMapAsync(HttpClient http, CancellationToken cancellationToken)
    {
        // This try block is used to handle HTTP errors mostly. It may be changed in the future how it's handled.

        try
        {
            Status("Fetching random track...");

            // Randomized URL is constructed with the ToUrl() method.
            var requestUrl = Config.Rules.RequestRules.ToUrl();

            Logger.LogDebug("Requesting generated URL: {url}", requestUrl);

            // HEAD request ensures least overhead
            using var randomResponse = await http.HeadAsync(requestUrl, cancellationToken);

            if (randomResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // The session is ALWAYS invalid if there's no map that can be found.
                // This DOES NOT relate to the lack of maps left that the user hasn't played.

                Logger.LogWarning("No map fulfills the randomization filter.");

                throw new InvalidSessionException();
            }

            randomResponse.EnsureSuccessStatusCode(); // Handles server issues, should normally retry


            // Following code gathers the track ID from the HEAD response (and ensures everything makes sense)

            if (randomResponse.RequestMessage is null)
            {
                Logger.LogWarning("Response from the HEAD request does not contain information about the request message. This is odd...");
                return;
            }

            if (randomResponse.RequestMessage.RequestUri is null)
            {
                Logger.LogWarning("Response from the HEAD request does not contain information about the request URI. This is odd...");
                return;
            }

            var trackId = randomResponse.RequestMessage.RequestUri.Segments.LastOrDefault();

            if (trackId is null)
            {
                Logger.LogWarning("Request URI does not contain any segments. This is very odd...");
                return;
            }


            // With the ID, it is possible to immediately download the track Gbx and process it with GBX.NET

            Status($"Downloading track {trackId}...");

            var trackGbxUrl = $"https://{randomResponse.RequestMessage.RequestUri.Host}/trackgbx/{trackId}";

            Logger.LogDebug("Downloading track on {trackGbxUrl}...", trackGbxUrl);

            using var trackGbxResponse = await http.GetAsync(trackGbxUrl, cancellationToken);

            trackGbxResponse.EnsureSuccessStatusCode();

            using var stream = await trackGbxResponse.Content.ReadAsStreamAsync(cancellationToken);


            // The map is gonna be parsed as it is downloading throughout

            Status("Parsing the map...");

            if (GameBox.ParseNode(stream) is not CGameCtnChallenge map)
            {
                Logger.LogWarning("Downloaded file is not a valid Gbx map file!");
                return;
            }


            // Map validation ensures that the player won't receive duplicate maps
            // + ensures some additional filters like "No Unlimiter", which cannot be filtered on TMX

            Status("Validating the map...");

            if (!ValidateMap(map))
            {
                // Attempts another track if invalid
                RequestAttempt++;
                Status($"Map choice is invalid (attempt {RequestAttempt}/{RequestMaxAttempts}).");

                if (RequestAttempt >= RequestMaxAttempts)
                {
                    Logger.LogWarning("Map choice is invalid after {RequestMaxAttempts} attempts. Cancelling the session...", RequestMaxAttempts);
                    RequestAttempt = 0;
                    throw new InvalidSessionException();
                }

                Logger.LogInformation("Map has not passed the validator, attempting another one...");
                await Task.Delay(500, cancellationToken);
                return;
            }

            // The map is saved to the defined DownloadedDirectoryPath using the FileName provided in ContentDisposition

            Status("Saving the map...");

            if (DownloadedDirectoryPath is null)
            {
                throw new Exception("Cannot update autosaves without a valid user data directory path.");
            }

            Logger.LogDebug("Ensuring {dir} exists...", DownloadedDirectoryPath);
            Directory.CreateDirectory(DownloadedDirectoryPath); // Ensures the directory really exists

            Logger.LogDebug("Preparing the file name...");

            // Extracts the file name, and if it fails, it uses the MapUid as a fallback
            var fileName = trackGbxResponse.Content.Headers.ContentDisposition?.FileName?.Trim('\"') ?? $"{map.MapUid}.Challenge.Gbx";

            // Validates the file name and fixes it if needed
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }

            CurrentSessionMapSavePath = Path.Combine(DownloadedDirectoryPath, fileName);

            Logger.LogInformation("Saving the map as {fileName}...", CurrentSessionMapSavePath);

            // WriteAllBytesAsync is used instead of GameBox.Save to ensure 1:1 data of the original map
            var trackData = await trackGbxResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(CurrentSessionMapSavePath, trackData, cancellationToken);

            Logger.LogInformation("Map saved successfully!");

            CurrentSessionMap = new SessionMap(map, randomResponse.Headers.Date ?? DateTimeOffset.Now); // The map should be ready to be played now

            CurrentSessionData?.Maps.Add(new()
            {
                Name = TextFormatter.Deformat(map.MapName),
                Uid = map.MapUid,
                TmxLink = randomResponse.RequestMessage.RequestUri.ToString()
            });
            SaveSessionData(); // May not be super necessary?
        }
        catch (HttpRequestException)
        {
            Status("Failed to fetch a track. Retrying...");
            await Task.Delay(1000, cancellationToken);
            return;
        }
        catch (InvalidSessionException)
        {
            Logger.LogWarning("Session is invalid.");
            throw;
        }
        catch (TaskCanceledException)
        {
            Logger.LogInformation("Session terminated during map request.");
            throw;
        }
        catch (Exception ex)
        {
            Status("Error! Check the log for more details.");
            Logger.LogError(ex, "An error occurred during map request.");
            
            await Task.Delay(1000, cancellationToken);

            return;
        }

        // Hacky last moment validations

        if (CurrentSessionMapSavePath is null)
        {
            return;
        }

        if (CurrentSessionWatch is null)
        {
            throw new UnreachableException("CurrentSessionWatch is null");
        }

        // Map starts here

        Status("Starting the map...");

        OpenFileIngame(CurrentSessionMapSavePath);

        CurrentSessionWatch.Start();

        SkipTokenSource = new CancellationTokenSource();
        MapStarted?.Invoke();

        Status("Playing the map...");

        // This loop either softly stops when the map is skipped by the player
        // or hardly stops when author medal is received / time limit is reached, End Session was clicked or an exception was thrown in general

        // SkipTokenSource is used within the session to skip a map, while CurrentSessionTokenSource handles the whole session cancellation

        while (!SkipTokenSource.IsCancellationRequested)
        {
            if (CurrentSessionWatch.Elapsed >= Config.Rules.TimeLimit) // Time limit reached case
            {
                if (CurrentSessionTokenSource is null)
                {
                    throw new UnreachableException("CurrentSessionTokenSource is null");
                }

                // Will cause the Task.Delay below to throw a cancellation exception
                // Code outside of the while loop wont be reached
                CurrentSessionTokenSource.Cancel();
            }

            await Task.Delay(20, cancellationToken);
        }

        CurrentSessionWatch.Stop(); // Time is paused until the next map starts

        if (isActualSkipCancellation) // When its a manual skip and not an automated skip by author medal receive
        {
            Status("Skipping the map...");

            // Apply the rules related to manual map skip
            // This part has a scripting potential too if properly implmented

            Skipped(CurrentSessionMap);

            isActualSkipCancellation = false;
        }

        Status("Ending the map...");

        StopTrackingCurrentSessionMap();

        // Map is no longer tracked at this point
    }

    /// <summary>
    /// Checks if the map hasn't been already played or if it follows current session rules.
    /// </summary>
    /// <param name="map"></param>
    /// <returns>True if valid, false if not valid.</returns>
    private static bool ValidateMap(CGameCtnChallenge map)
    {
        if (Autosaves.ContainsKey(map.MapUid))
        {
            return false;
        }

        if (Config.Rules.NoUnlimiter && map.Chunks.TryGet(0x3F001000, out _))
        {
            return false;
        }

        return true;
    }

    private static void StopTrackingCurrentSessionMap()
    {
        SkipTokenSource = null;
        CurrentSessionMap = null;
        CurrentSessionMapSavePath = null;
        MapEnded?.Invoke();
    }

    /// <summary>
    /// MANUAL end of session.
    /// </summary>
    /// <returns></returns>
    public static async Task EndSessionAsync()
    {
        if (SessionEnding)
        {
            return;
        }

        SessionEnding = true;

        Status("Ending the session...");

        CurrentSessionTokenSource?.Cancel();

        if (CurrentSession is null)
        {
            return;
        }

        try
        {
            await CurrentSession; // Kindly waits until the session considers it was cancelled. ClearCurrentSession is called within it.
        }
        catch (TaskCanceledException)
        {
            ClearCurrentSession(); // Just an ensure
        }

        SessionEnding = false;
    }

    public static Task SkipMapAsync()
    {
        Status("Requested to skip the map...");
        isActualSkipCancellation = true;
        SkipTokenSource?.Cancel();
        return Task.CompletedTask;
    }

    public static void OpenFileIngame(string filePath)
    {
        if (Config.GameDirectory is null)
        {
            throw new Exception("Game directory is null");
        }

        Logger.LogInformation("Opening {filePath} in TMForever...", filePath);

        var startInfo = new ProcessStartInfo(Path.Combine(Config.GameDirectory, Constants.TmForeverExe), $"/useexedir /singleinst /file=\"{filePath}\"");

        var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();
        process.WaitForInputIdle();
    }

    public static void OpenAutosaveIngame(string fileName)
    {
        if (AutosavesDirectoryPath is null)
        {
            throw new Exception("Cannot open an autosave ingame without a valid user data directory path.");
        }

        OpenFileIngame(Path.Combine(AutosavesDirectoryPath, fileName));
    }

    public static void Exit()
    {
        Logger.LogInformation("Exiting...");
        FlushLog();
        Environment.Exit(0);
    }

    public static void ReloadMap()
    {
        Logger.LogInformation("Reloading the map...");

        if (CurrentSessionMapSavePath is not null)
        {
            OpenFileIngame(CurrentSessionMapSavePath);
        }
    }

    public static void FlushLog()
    {
        LogWriter.Flush();
    }

    public static async Task FlushLogAsync()
    {
        await LogWriter.FlushAsync();
    }
}
