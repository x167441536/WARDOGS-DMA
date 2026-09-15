using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Windows.Forms;
using System.Drawing.Imaging;
using Vmmsharp;
using ImGuiNET;
using Veldrid;
using Point = System.Drawing.Point;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp.PixelFormats;

internal static class Program
{
    private const string DefaultProcess = "WardogsClient-Win64-Shipping.exe";
    private const string DefaultDevice = "fpga";
    private const string BuildTag = "radar-perf-marker-columns-loglevel-v4-local-anchor";

    // These values are image RVAs supplied for the WardogsClient build.
    private const ulong GNames = Offsets.GNames;
    private const ulong GWorld = Offsets.GWorld;
    private const ulong GObjects = Offsets.GObjects;
    private const ulong NumElements = Offsets.NumElements;
    private const ulong GEngine = Offsets.GEngine;

    private static readonly EspSettings Settings = EspSettings.Load();

    // 0=关闭，1=仅错误，2=常规状态，3=详细诊断。
    private static void LogMessage(int level, string message)
    {
        if (Settings.LogLevel >= level)
            Console.WriteLine(message);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            PrintUsage();
            return 64;
        }
        Console.WriteLine("Wardogs DMA reader (read-only)");
        Console.WriteLine($"Process: {options.ProcessName}");
        Console.WriteLine($"Device:  {options.Device}");

        // Native VMM dependencies are copied beside the executable. Setting the
        // directory explicitly also makes running from another working directory reliable.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Settings.Save();

        try
        {
            var vmmArgs = new List<string> { "-device", options.Device };
            if (options.WaitInitialize)
                vmmArgs.Add("-waitinitialize");

            using var vmm = new Vmm(vmmArgs.ToArray());
            var (process, actualProcessName, moduleBase) = WaitForGameProcess(vmm, options.ProcessName);

            Console.WriteLine($"Module base: 0x{moduleBase:X}");
            ValidatePeImage(process, moduleBase);

            var namesValue = ReadGlobal(process, moduleBase, "GNames", GNames, pointer: false);
            var worldValue = ReadGlobal(process, moduleBase, "GWorld", GWorld, pointer: true);
            var objectsValue = ReadGlobal(process, moduleBase, "GObjects", GObjects, pointer: false);
            ReadGlobal(process, moduleBase, "GEngine", GEngine, pointer: true);

            var numElementsAddress = moduleBase + NumElements;
            var numElements = ReadInt32(process, numElementsAddress);
            Console.WriteLine($"NumElements @ 0x{numElementsAddress:X}: {numElements} (0x{unchecked((uint)numElements):X8})");
            if (numElements < 0 || numElements > 100_000_000)
                Console.WriteLine("NumElements validation: value is outside the usual UE object range.");

            // GNames is an inline UE5 FNamePool structure at this RVA; its
            // first qword may legitimately be zero.  GWorld/GObjects are the
            // pointer globals that must be initialized before constructing
            // the tracker.
            if (worldValue == 0 || objectsValue == 0 || numElements <= 0)
            {
                Console.WriteLine("正在等待游戏地图初始化 UE 全局对象 …");
                (namesValue, worldValue, objectsValue, numElements) = WaitForRuntimeGlobals(process, moduleBase);
            }

            DumpGlobalBytes(process, moduleBase, "GNames", GNames, 64);
            DumpGlobalBytes(process, moduleBase, "GObjects", GObjects, 64);
            ReadObjectArraySample(process, moduleBase, numElements);
            if (options.ReadNameSample)
                ReadNamePoolSample(process, moduleBase);

            if (options.Once)
            {
                using var diagnosticTracker = new EspTracker(process, moduleBase, Settings);
                Console.WriteLine($"Wardogs build: {BuildTag}; FTransformSize=0x{Offsets.FTransformSize:X}; FTransformTranslation=0x{Offsets.FTransformTranslation:X}; skeletonDefault={Settings.ShowSkeleton}");
                Console.WriteLine(diagnosticTracker.TargetClassStatus);
                diagnosticTracker.DumpRuntimeChain(worldValue);
                var scan = diagnosticTracker.ScanOnce();
                Console.WriteLine($"ESP diagnostic: {scan.Status}");
                Console.WriteLine($"ESP levels={scan.LevelCount}, actors={scan.ActorCount}, players={scan.PlayerCount}, vehicles={scan.VehicleCount}, projected={scan.Items.Count}");
                Console.WriteLine($"ESP sources: levelActors={scan.LevelActorCount}, playerStates={scan.PlayerStateCount}, playerPawns={scan.PlayerPawnCount}, localPawns={scan.LocalPawnCount}, globalVehicles={scan.GlobalVehicleCount}");
                Console.WriteLine($"ESP team metadata: {diagnosticTracker.TeamMetadataStatus}");
                Console.WriteLine($"ESP DMA diagnostics: {diagnosticTracker.DmaDiagnostics}");
                if (scan.SampleClassNames.Count > 0)
                    Console.WriteLine($"ESP top classes: {string.Join(", ", scan.SampleClassNames)}");
                Console.WriteLine("Read completed.");
                Console.Out.Flush();
                Environment.Exit(0);
                return 0;
            }

            ApplicationConfiguration.Initialize();
            using var tracker = new EspTracker(process, moduleBase, Settings);
            Console.WriteLine($"Wardogs build: {BuildTag}; FTransformSize=0x{Offsets.FTransformSize:X}; FTransformTranslation=0x{Offsets.FTransformTranslation:X}; skeletonDefault={Settings.ShowSkeleton}");
            Console.WriteLine(tracker.TargetClassStatus);
            Console.WriteLine("ESP source warm-up running...");
            tracker.WarmUp();
            using var radar = new RadarForm(tracker, Settings);
            using var overlay = new EspOverlay(tracker, Settings, radar);
            if (Settings.RadarEnabled)
                overlay.Shown += (_, _) => { radar.Show(); radar.BringToFront(); };
            tracker.Start();
            Console.WriteLine("ESP overlay running. Close it with Alt+F4.");
            Application.Run(overlay);
            if (!radar.IsDisposed) radar.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DMA read failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 10;
        }
    }

    private static void ValidatePeImage(VmmProcess process, ulong moduleBase)
    {
        var header = ReadExact(process, moduleBase, 0x1000);
        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException("Module base does not contain an MZ header.");

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C, 4));
        if (peOffset < 0 || peOffset + 4 > header.Length ||
            header[peOffset] != (byte)'P' || header[peOffset + 1] != (byte)'E' ||
            header[peOffset + 2] != 0 || header[peOffset + 3] != 0)
            throw new InvalidDataException("Module base does not contain a valid PE signature.");

        Console.WriteLine($"PE validation: OK (e_lfanew=0x{peOffset:X})");
    }

    private static ulong ReadGlobal(VmmProcess process, ulong moduleBase, string name, ulong rva, bool pointer)
    {
        var address = moduleBase + rva;
        var bytes = ReadExact(process, address, 8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var suffix = pointer ? "pointer" : "raw/qword";
        Console.WriteLine($"{name} @ RVA 0x{rva:X8}, VA 0x{address:X}: 0x{value:X16} ({suffix})");
        return value;
    }

    private static (VmmProcess Process, string ProcessName, ulong ModuleBase) WaitForGameProcess(Vmm vmm, string requestedName)
    {
        var candidates = requestedName.Equals(DefaultProcess, StringComparison.OrdinalIgnoreCase)
            ? new[] { requestedName, "WardogsClient-Win64-Shipping_fixed.exe" }
            : new[] { requestedName };
        var lastMessage = DateTime.MinValue;
        while (true)
        {
            foreach (var candidate in candidates)
            {
                var process = vmm.Process(candidate);
                if (process is null) continue;
                var moduleBase = process.GetModuleBase(candidate);
                if (moduleBase != 0) return (process, candidate, moduleBase);
                if ((DateTime.UtcNow - lastMessage).TotalSeconds >= 1)
                {
                    Console.WriteLine($"正在等待游戏模块加载：{candidate} …");
                    lastMessage = DateTime.UtcNow;
                }
            }
            if ((DateTime.UtcNow - lastMessage).TotalSeconds >= 1)
            {
                Console.WriteLine($"正在查找游戏进程：{string.Join(" / ", candidates)} …");
                lastMessage = DateTime.UtcNow;
            }
            Thread.Sleep(1000);
        }
    }

    private static (ulong Names, ulong World, ulong Objects, int NumElements) WaitForRuntimeGlobals(VmmProcess process, ulong moduleBase)
    {
        var lastMessage = DateTime.MinValue;
        while (true)
        {
            try
            {
                var names = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(process, moduleBase + GNames, 8));
                var world = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(process, moduleBase + GWorld, 8));
                var objects = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(process, moduleBase + GObjects, 8));
                var count = ReadInt32(process, moduleBase + NumElements);
                if (world != 0 && objects != 0 && count > 0)
                    return (names, world, objects, count);
            }
            catch { }
            if ((DateTime.UtcNow - lastMessage).TotalSeconds >= 1)
            {
                Console.WriteLine("正在等待游戏地图初始化 UE 全局对象 …");
                lastMessage = DateTime.UtcNow;
            }
            Thread.Sleep(1000);
        }
    }

    private static int ReadInt32(VmmProcess process, ulong address)
    {
        var bytes = ReadExact(process, address, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static byte[] ReadExact(VmmProcess process, ulong address, uint size)
    {
        var data = process.MemRead(address, size, Vmm.FLAG_NOCACHE);
        if (data is null || data.Length != size)
            throw new IOException($"Incomplete read at 0x{address:X}: expected {size} bytes.");
        return data;
    }

    private static void DumpGlobalBytes(VmmProcess process, ulong moduleBase, string name, ulong rva, uint size)
    {
        var address = moduleBase + rva;
        var data = ReadExact(process, address, size);
        Console.WriteLine($"{name} bytes @ 0x{address:X}: {Convert.ToHexString(data)}");
    }

    private static void ReadNamePoolSample(VmmProcess process, ulong moduleBase)
    {
        var poolAddress = moduleBase + GNames;
        var bytes = ReadExact(process, poolAddress, 0x40);
        Console.WriteLine($"NamePoolData sample @ 0x{poolAddress:X}: {Convert.ToHexString(bytes)}");

        try
        {
            // Resolve a live UClass name instead of assuming name ID zero is a
            // normal entry. Some UE5 builds reserve data at the start of block 0.
            var engine = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(process, moduleBase + GEngine, 8));
            var engineClass = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(process, engine + Offsets.UObjectClass, 8));
            var classNameId = BinaryPrimitives.ReadUInt32LittleEndian(ReadExact(process, engineClass + Offsets.ClassNameId, 4));
            var className = new NamePool(process, poolAddress).Resolve(classNameId);
            var note = className.StartsWith("Name_", StringComparison.Ordinal)
                ? " (direct text decode unavailable; ESP classification uses UClass pointers)"
                : string.Empty;
            Console.WriteLine($"NamePool live class sample: id=0x{classNameId:X8}, text=\"{className}\"{note}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NamePool entry sample unavailable: {ex.Message}");
        }
    }

    private static void ReadObjectArraySample(VmmProcess process, ulong moduleBase, int numElements)
    {
        var objectsAddress = moduleBase + GObjects;
        try
        {
            // TUObjectArray layout used by the supplied SDK:
            // Objects (+0x00) -> chunk pointer table, NumElements (+0x14),
            // NumChunks (+0x1C). Each FUObjectItem is 0x18 bytes and its
            // UObject pointer is at +0x10 after clearing the chunk tag bits.
            var chunks = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(process, objectsAddress, 8));
            var numChunks = ReadInt32(process, objectsAddress + Offsets.ObjectArrayNumChunks);
            if (chunks == 0 || numChunks <= 0)
            {
                Console.WriteLine($"GObjects chunk table: null or empty (chunks=0x{chunks:X}, count={numChunks}).");
                return;
            }

            var firstChunk = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(process, chunks, 8));
            if (firstChunk == 0)
            {
                Console.WriteLine($"GObjects chunk table @ 0x{chunks:X}: first chunk is null.");
                return;
            }

            var normalizedChunk = firstChunk & ~0xFUL;
            var firstObject = BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(process, normalizedChunk + Offsets.ObjectItemObject, 8));
            Console.WriteLine($"GObjects: chunks=0x{chunks:X}, firstChunk=0x{firstChunk:X}, firstObject=0x{firstObject:X}");
            if (numElements > 0 && firstObject != 0)
                Console.WriteLine("GObjects first entry read: OK (8-byte object pointer).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GObjects entry sample unavailable: {ex.Message}");
        }
    }

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--process" when i + 1 < args.Length:
                    options.ProcessName = args[++i];
                    break;
                case "--device" when i + 1 < args.Length:
                    options.Device = args[++i];
                    break;
                case "--no-wait":
                    options.WaitInitialize = false;
                    break;
                case "--name-sample":
                    options.ReadNameSample = true;
                    break;
                case "--once":
                    options.Once = true;
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        return options;
    }

    private static void PrintUsage() => Console.WriteLine(
        "Usage: WardogsReader.exe [--process NAME] [--device fpga] [--no-wait] [--name-sample] [--once]");

    private sealed class Options
    {
        public string ProcessName { get; set; } = DefaultProcess;
        public string Device { get; set; } = DefaultDevice;
        public bool WaitInitialize { get; set; } = true;
        public bool ReadNameSample { get; set; }
        public bool Once { get; set; }
    }

    private sealed class EspSettings
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, WriteIndented = true };
        private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "wardogs-esp.json");

        public EspSettings() { }
        public volatile bool Enabled = true;
        public volatile bool ShowPlayers = true;
        public volatile bool ShowVehicles = true;
        public volatile bool ShowFriendlyVehicles = true;
        public volatile bool ShowEnemyVehicles = true;
        public volatile bool ShowTeammates = false;
        public volatile bool ShowEnemies = true;
        public volatile bool ShowDowned = true;
        public volatile bool ShowNames = true;
        public volatile bool ShowDistance = true;
        // 0 means run the frame reader without an artificial 60 Hz cap.
        public volatile int ReadIntervalMs = 0;
        // 0 means no artificial overlay render-rate limit.
        public int RenderFpsLimit = 0;
        public volatile int SourceRefreshMs = 250;
        public volatile int VehicleRefreshMs = 2000;
        public volatile bool ShowHealthBar = true;
        // Skeleton pose arrays are considerably more expensive than root
        // positions. Keep them opt-in so a crowded match does not throttle
        // the radar reader before the user explicitly needs them.
        // Keep skeleton ESP enabled by default.  Existing config files still
        // win, but a fresh reader must not silently produce bonePlayers=0 by
        // disabling the entire pose path.
        public volatile bool ShowSkeleton = true;
        public volatile bool ShowWeapons = true;
        // Circular player-only radar rendered inside the ESP overlay.
        public volatile bool EspMiniRadarEnabled = true;
        public volatile float EspMiniRadarRadius = 150f;
        // World units (Wardogs uses centimetres); 100000 = 1 km.
        public volatile float EspMiniRadarRange = 100000f;
        public volatile bool EspMiniRadarShowNames = true;
        public volatile bool EspMiniRadarShowHeading = true;
        public volatile bool EspMiniRadarShowTeammates = true;
        public volatile bool EspMiniRadarShowEnemies = true;
        // 0=Off, 1=Errors, 2=Normal, 3=Verbose.
        public volatile int LogLevel = 2;
        public volatile bool RadarEnabled = true;
        public volatile bool RadarShowNames = true;
        public volatile bool RadarShowVehicles = true;
        public volatile bool RadarShowTeammates = true;
        public volatile bool RadarShowEnemies = true;
        public volatile bool RadarShowWeapons = true;
        public volatile bool RadarRotateWithLocal = false;
        public volatile float RadarZoom = 1.0f;
        public string RadarMapId = "bakurani";
        public string RadarMapDirectory = string.Empty;
        public volatile int FriendlyColorArgb = Color.LimeGreen.ToArgb();
        public volatile int EnemyColorArgb = Color.Red.ToArgb();
        public volatile int FriendlyVehicleColorArgb = Color.DeepSkyBlue.ToArgb();
        public volatile int EnemyVehicleColorArgb = Color.OrangeRed.ToArgb();
        public volatile int DownedColorArgb = Color.Orange.ToArgb();
        public volatile int UnknownColorArgb = Color.Gold.ToArgb();

        public static EspSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var loaded = JsonSerializer.Deserialize<EspSettings>(File.ReadAllText(ConfigPath), JsonOptions);
                    if (loaded is not null)
                    {
                        loaded.Normalize();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ESP config load skipped: {ex.GetType().Name}: {ex.Message}");
            }
            return new EspSettings();
        }

        public void Save()
        {
            try
            {
                Normalize();
                var temp = ConfigPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(temp, ConfigPath, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ESP config save skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Normalize()
        {
            ReadIntervalMs = Math.Clamp(ReadIntervalMs, 0, 1000);
            RenderFpsLimit = Math.Clamp(RenderFpsLimit, 0, 1000);
            SourceRefreshMs = Math.Clamp(SourceRefreshMs, 250, 60000);
            VehicleRefreshMs = Math.Clamp(VehicleRefreshMs, 250, 60000);
            RadarZoom = Math.Clamp(RadarZoom, 0.25f, 30f);
            EspMiniRadarRadius = Math.Clamp(EspMiniRadarRadius, 80f, 320f);
            EspMiniRadarRange = Math.Clamp(EspMiniRadarRange, 10000f, 1000000f);
            LogLevel = Math.Clamp(LogLevel, 0, 3);
        }
    }

    private sealed class EspTracker : IDisposable
    {
        private readonly VmmProcess _process;
        private readonly ulong _base;
        private readonly EspSettings _settings;
        private readonly NamePool _names;
        private readonly ulong _playerBaseClass;
        private readonly ulong _vehicleBaseClass;
        private readonly ulong _modularVehicleClass;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _sync = new();
        // Serialize high-level DMA batches across reader threads.
        private readonly SemaphoreSlim _dmaGate = new(1, 1);
        private long _dmaFrameSkips;
        private long _dmaWaitCount;
        private double _dmaWaitMs;
        private double _lastSourceMs;
        private double _lastVehicleMs;
        private long _lastWorldTransformCount;
        private ulong _lastWorldTransformOffset;
        private bool _lastVehicleCameraView;
        private readonly object _sourceSync = new();
        private readonly object _identityRefreshSync = new();
        private readonly ConcurrentDictionary<ulong, ActorClassInfo> _classInfo = new();
        // Skeleton pose descriptors are stable per USkeletalMeshComponent. Cache
        // the discovered array location/count to avoid probing several candidate
        // offsets on every render frame (the previous implementation caused
        // avoidable DMA traffic and visible ESP stutter).
        private readonly ConcurrentDictionary<ulong, (ulong Pose, int Count, int Stride, int Translation, ulong Offset, bool Composed)> _poseCache = new();
        private readonly ConcurrentDictionary<ulong, (ulong Asset, DateTime Stamp)> _meshAssetCache = new();
        // Selected ComponentToWorld slot per component.  The SDK omits this
        // private member, therefore probe the documented alignment candidates
        // once and reuse the winner for subsequent frames.
        private readonly ConcurrentDictionary<ulong, ulong> _componentTransformOffsetCache = new();
        private readonly ConcurrentDictionary<ulong, (DateTime Stamp, DynamicBoneMap Map)> _boneMapCache = new();
        private readonly ConcurrentDictionary<ulong, DateTime> _boneMapDiag = new();
        private readonly ConcurrentDictionary<ulong, byte> _boneMapFailureDiag = new();
        private readonly ConcurrentDictionary<ulong, DateTime> _poseMissCache = new();
        private readonly ConcurrentDictionary<ulong, (DateTime Stamp, int Count, Dictionary<int, Vector3> Relative, ComponentTransform Component)> _skeletonCache = new();
        // Adapt the pose refresh cadence to crowd size. Root transforms still
        // update every scan; only the expensive animated pose snapshot slows
        // down when many players are present.
        private volatile int _skeletonCacheWindowMs = 100;
        private long _skeletonCalls;
        private long _skeletonSuccesses;
        private long _skeletonFailures;
        private long _skeletonCacheHits;
        private DateTime _lastSkeletonDiagUtc = DateTime.MinValue;
        private string _lastSkeletonDiag = "none";
        private readonly object _skeletonDiagSync = new();
        private readonly object _skeletonPrefilterSync = new();
        private HashSet<ulong> _previousVisibleSkeletonActors = new();
        private readonly Dictionary<ulong, byte[]> _framePoseBuffers = new();
        private long _lastScatterAddressCount;
        private long _lastScatterBonePlayers;
        private double _lastScatterMs;
        private readonly Dictionary<ulong, ulong> _superByClass = new();
        private readonly HashSet<ulong> _vehicleClasses = new();
        private HashSet<ulong> _levelTargetActors = new();
        private HashSet<ulong> _playerSourceActors = new();
        private HashSet<ulong> _globalVehicleActors = new();
        private HashSet<ulong> _levelVehicleActors = new();
        private HashSet<ulong> _identityActors = new();
        private IReadOnlyList<ActorIdentity> _targetIdentities = Array.Empty<ActorIdentity>();
        private int _levelCount;
        private int _levelActorCount;
        private int _playerStateCount;
        private int _playerPawnCount;
        private int _localPawnCount;
        private ulong _cachedController;
        private ulong _localPawn;
        // The controller switches from the character pawn to the mortar or
        // vehicle pawn while the weapon view is active. Keep that actor as a
        // separate local anchor so radar and marker calculations follow the
        // object that actually owns the current view.
        private ulong _activeLocalActor;
        private Vector3 _lastLocalWorldPosition;
        private float _lastLocalYaw;
        private bool _hasLastLocalWorldPosition;
        // Possessing a mortar temporarily changes Controller.Pawn to the
        // mortar vehicle. Retain the last character pawn so the radar keeps a
        // stable local anchor while the player is operating the weapon.
        private ulong _lastLocalPlayerPawn;
        private DateTime _lastIdentityRefreshUtc = DateTime.MinValue;
        private IReadOnlyList<string> _sourceClassNames = Array.Empty<string>();
        private volatile bool _sourceRefreshPending;
        private Thread? _thread;
        private Thread? _sourceThread;
        private Thread? _vehicleThread;
        private IReadOnlyList<EspItem> _items = Array.Empty<EspItem>();
        // Radar contacts deliberately have a separate cache from the ESP
        // snapshot.  ESP is projection/viewport filtered; the radar must keep
        // every classified actor even when it is off-screen or far away.
        private IReadOnlyList<RadarItem> _radarItems = Array.Empty<RadarItem>();
        private Dictionary<ulong, PlayerVisualMeta> _playerMeta = new();
        private readonly Dictionary<ulong, string> _playerNameCache = new();
        private readonly Dictionary<ulong, string> _weaponNameCache = new();
        private readonly Dictionary<ulong, string> _actorWeaponCache = new();
        private DateTime _lastWeaponRefreshUtc = DateTime.MinValue;
        private int _localTeam = -1;
        private ulong _localFactionKey;
        private string _localFactionName = string.Empty;
        private DateTime _lastMetadataRefreshUtc = DateTime.MinValue;
        private DateTime _lastProjectionDiagUtc = DateTime.MinValue;
        private DateTime _lastCameraDiagUtc = DateTime.MinValue;
        private DateTime _lastAdsDiagUtc = DateTime.MinValue;
        private float _lastAdsMultiplier = 1f;
        private bool _hasAdsMultiplier;
        private float _lastObservedOpticFov;
        private DateTime _lastObservedOpticUtc = DateTime.MinValue;
        private DateTime _lastAdsComponentScanUtc = DateTime.MinValue;
        private float _lastControlYaw;
        private bool _hasControlYaw;
        private CameraState _lastCameraState;
        private DateTime _lastCameraValidUtc = DateTime.MinValue;
        private int _cameraDebugPrinted;
        private bool _hasLastCamera;
        private int _lastCameraIndex = -1;
        private CameraState _pendingCamera;
        private int _pendingCameraIndex = -1;
        private int _pendingCameraStreak;
        // Last accepted camera transform.  The game occasionally exposes a
        // torn cache entry while an optic is animating; retaining the previous
        // transform for one frame prevents the ESP projection from jumping
        // across the screen.
        private double _readFps;
        private double _readAverageMs;
        private double _lastFrameReadMs;
        private double _lastActorFrameMs;
        private double _lastPoseBatchMs;
        private volatile int _dmaBenchmarkRequested;
        private string _dmaBenchmarkStatus = "未运行";
        private readonly object _dmaBenchmarkSync = new();

        public EspTracker(VmmProcess process, ulong moduleBase, EspSettings settings)
        {
            _process = process;
            _base = moduleBase;
            _settings = settings;
            _names = new NamePool(process, moduleBase + GNames);
            _playerBaseClass = ReadUObjectByIndex(Offsets.PlayerBaseClassIndex);       // WDMoverCharacter
            _vehicleBaseClass = ReadUObjectByIndex(Offsets.VehicleBaseClassIndex);      // BHBaseVehiclePawn
            _modularVehicleClass = ReadUObjectByIndex(Offsets.ModularVehicleClassIndex);   // ModularVehicle
        }

        public IReadOnlyList<EspItem> Snapshot { get { lock (_sync) return _items; } }
        public IReadOnlyList<RadarItem> RadarSnapshot { get { lock (_sync) return _radarItems; } }
        public ulong LocalPawn { get { lock (_sourceSync) return _localPawn; } }
        public ulong ActiveLocalActor { get { lock (_sourceSync) return _activeLocalActor; } }
        public bool TryGetLastLocalWorld(out Vector3 location, out float yaw)
        {
            lock (_sourceSync)
            {
                location = _lastLocalWorldPosition;
                yaw = _lastLocalYaw;
                return _hasLastLocalWorldPosition;
            }
        }
        public Vector3 CameraLocation => _lastCameraState.Location;
        public Vector3 CameraRotation => new(_lastCameraState.Rotation.X,
            _hasControlYaw ? _lastControlYaw : _lastCameraState.Rotation.Y,
            _lastCameraState.Rotation.Z);
        public double ReadFps => Volatile.Read(ref _readFps);
        public double ReadAverageMs => Volatile.Read(ref _readAverageMs);
        public string DmaDiagnostics
        {
            get
            {
                lock (_dmaBenchmarkSync)
                    return $"frame={_lastFrameReadMs:0.00}ms actor={_lastActorFrameMs:0.00}ms poseSG={_lastPoseBatchMs:0.00}ms source={_lastSourceMs:0.00}ms vehicle={_lastVehicleMs:0.00}ms worldXform={_lastWorldTransformCount}@0x{_lastWorldTransformOffset:X} vehicleView={_lastVehicleCameraView} sg={_lastScatterMs:0.00}ms addresses={_lastScatterAddressCount} bones={_lastScatterBonePlayers} dmaWaitTotal={_dmaWaitMs:0.00}ms/{_dmaWaitCount} frameSkip={_dmaFrameSkips} benchmark={_dmaBenchmarkStatus}";
            }
        }
        private bool TryEnterDma(int timeoutMs, bool framePriority = false)
        {
            var started = Stopwatch.GetTimestamp();
            if (!_dmaGate.Wait(timeoutMs))
            {
                if (framePriority) Interlocked.Increment(ref _dmaFrameSkips);
                return false;
            }
            var waited = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (waited > 0.05)
            {
                Interlocked.Increment(ref _dmaWaitCount);
                lock (_dmaBenchmarkSync) _dmaWaitMs += waited;
            }
            return true;
        }
        private void ExitDma() => _dmaGate.Release();
        public void RequestDmaBenchmark() => Interlocked.Exchange(ref _dmaBenchmarkRequested, 1);
        public string TargetClassStatus =>
            $"ESP target classes: WDMoverCharacter=0x{_playerBaseClass:X}, BHBaseVehiclePawn=0x{_vehicleBaseClass:X}, ModularVehicle=0x{_modularVehicleClass:X}";
        public string TeamMetadataStatus
        {
            get
            {
                lock (_sourceSync)
                {
                    var known = _playerMeta.Values.Count(x => x.Team >= 0);
                    var downed = _playerMeta.Values.Count(x => x.Downed);
                    var teams = string.Join(",", _playerMeta.Values.Where(x => x.Team >= 0)
                        .GroupBy(x => x.Team).OrderBy(x => x.Key)
                        .Select(x => $"{x.Key}:{x.Count()}"));
                    var factions = string.Join(",", _playerMeta.Values.Where(x => !string.IsNullOrWhiteSpace(x.FactionName))
                        .GroupBy(x => x.FactionName, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(x => $"{x.Key}:{x.Count()}"));
                    return $"known={known}/{_playerMeta.Count}, downed={downed}, localTeam={_localTeam}, localFaction={_localFactionName}, teams=[{teams}], factions=[{factions}]";
                }
            }
        }

        public void Start()
        {
            _sourceThread = new Thread(SourceLoop) { IsBackground = true, Name = "Wardogs actor source reader" };
            _vehicleThread = new Thread(VehicleLoop) { IsBackground = true, Name = "Wardogs vehicle object reader" };
            _thread = new Thread(Loop) { IsBackground = true, Name = "Wardogs ESP reader" };
            // Keep the high-frequency transform reader responsive when the
            // slower source/object enumeration threads are refreshing large
            // UE arrays.  This only changes scheduler priority; all DMA
            // access remains read-only and the existing scatter batches are
            // preserved.
            try
            {
                _thread.Priority = ThreadPriority.AboveNormal;
                _sourceThread.Priority = ThreadPriority.BelowNormal;
                _vehicleThread.Priority = ThreadPriority.BelowNormal;
            }
            catch (PlatformNotSupportedException) { }
            _sourceThread.Start();
            _vehicleThread.Start();
            _thread.Start();
        }

        public void WarmUp()
        {
            var world = ReadPtr(_base + GWorld);
            if (world == 0) return;
            RefreshPlayerSources(world);
            RefreshLevelSources(world);
            // The first player-array snapshot can arrive before streamed
            // level actors are registered. Run a second metadata pass after
            // level discovery so faction state is available immediately,
            // without requiring the in-game scoreboard to be opened.
            _lastMetadataRefreshUtc = DateTime.MinValue;
            RefreshPlayerSources(world);
            RefreshGlobalVehicleActors();
            RefreshTargetIdentities(force: true);
        }

        private void SourceLoop()
        {
            var nextLevelRefresh = DateTime.UtcNow;
            while (!_stop.IsCancellationRequested)
            {
                if (!TryEnterDma(100))
                {
                    LogMessage(3, "ESP DMA scheduler: source refresh deferred (frame reader owns queue)");
                    if (_stop.Token.WaitHandle.WaitOne(25)) break;
                    continue;
                }
                var sourceTimer = Stopwatch.StartNew();
                try
                {
                    var world = ReadPtr(_base + GWorld);
                    if (Interlocked.Exchange(ref _dmaBenchmarkRequested, 0) != 0)
                        RunDmaBenchmark();
                    if (world != 0)
                    {
                        RefreshPlayerSources(world);
                        if (DateTime.UtcNow >= nextLevelRefresh)
                        {
                            // Request the expensive streamed-level refresh,
                            // but perform it outside this DMA critical section
                            // below.  PlayerState updates stay short and do not
                            // hold the queue for the full multi-level scan.
                            _sourceRefreshPending = true;
                            nextLevelRefresh = DateTime.UtcNow.AddMilliseconds(Math.Clamp(Math.Max(_settings.SourceRefreshMs, 1000), 1000, 60000));
                        }
                        RefreshTargetIdentities();
                    }
                }
                catch (Exception ex)
                {
                    LogMessage(1, $"ESP source scan error: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    sourceTimer.Stop();
                    _lastSourceMs = sourceTimer.Elapsed.TotalMilliseconds;
                    ExitDma();
                }
                if (_sourceRefreshPending && TryEnterDma(100))
                {
                    var levelTimer = Stopwatch.StartNew();
                    try
                    {
                        var world = ReadPtr(_base + GWorld);
                        if (world != 0)
                        {
                            RefreshLevelSources(world);
                            _lastMetadataRefreshUtc = DateTime.MinValue;
                            _sourceRefreshPending = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage(1, $"ESP level source scan error: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        levelTimer.Stop();
                        _lastSourceMs = Math.Max(_lastSourceMs, levelTimer.Elapsed.TotalMilliseconds);
                        ExitDma();
                    }
                }
                // Keep the high-frequency frame reader from competing with
                // source enumeration on the DMA device. Actor transforms are
                // still sampled every frame; source membership need not be.
                var sourceDelay = Math.Clamp(_settings.SourceRefreshMs, 500, 60000);
                if (_stop.Token.WaitHandle.WaitOne(sourceDelay)) break;
            }
        }

        private void VehicleLoop()
        {
            // WarmUp performs the initial object scan before the overlay is
            // shown; wait before the first periodic refresh to avoid duplicate
            // GObjects traffic during startup.
            if (_stop.Token.WaitHandle.WaitOne(Math.Clamp(_settings.VehicleRefreshMs, 250, 60000))) return;
            while (!_stop.IsCancellationRequested)
            {
                if (!TryEnterDma(100))
                {
                    LogMessage(3, "ESP DMA scheduler: vehicle refresh deferred (frame reader owns queue)");
                    if (_stop.Token.WaitHandle.WaitOne(25)) break;
                    continue;
                }
                var vehicleTimer = Stopwatch.StartNew();
                try
                {
                    RefreshGlobalVehicleActors();
                    RefreshTargetIdentities();
                }
                catch (Exception ex) { LogMessage(1, $"ESP global vehicle scan error: {ex.GetType().Name}: {ex.Message}"); }
                finally
                {
                    vehicleTimer.Stop();
                    _lastVehicleMs = vehicleTimer.Elapsed.TotalMilliseconds;
                    ExitDma();
                }
                if (_stop.Token.WaitHandle.WaitOne(Math.Clamp(_settings.VehicleRefreshMs, 250, 60000))) break;
            }
        }

        private void Loop()
        {
            var reportTimer = Stopwatch.StartNew();
            var frameCount = 0;
            var totalScanMilliseconds = 0.0;
            var maxScanMilliseconds = 0.0;
            while (!_stop.IsCancellationRequested)
            {
                var frameTimer = Stopwatch.StartNew();
                if (!TryEnterDma(1, framePriority: true))
                {
                    // Avoid a zero-timeout spin loop.  The previous loop could
                    // increment frameSkip millions of times per second while
                    // a source/object scan owned the FPGA queue.
                    if (_stop.Token.WaitHandle.WaitOne(1)) break;
                    continue;
                }
                try
                {
                    var scanStarted = Stopwatch.GetTimestamp();
                    var frame = ScanFrame();
                    _lastFrameReadMs = Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds;
                    lock (_sync)
                    {
                        // Camera caches can be empty for a few frames while
                        // switching into a high-power optic. Keep the last
                        // valid primitives instead of replacing the opaque
                        // full-screen overlay with an empty snapshot.
                        if (!(frame.Status == "camera unavailable" && _items.Count > 0))
                            _items = frame.Items;
                    }
                    frameTimer.Stop();
                    frameCount++;
                    totalScanMilliseconds += frameTimer.Elapsed.TotalMilliseconds;
                    maxScanMilliseconds = Math.Max(maxScanMilliseconds, frameTimer.Elapsed.TotalMilliseconds);
                    if (reportTimer.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        var classes = frame.SampleClassNames.Count == 0
                            ? "none"
                            : string.Join(", ", frame.SampleClassNames);
                        var scanFps = frameCount / reportTimer.Elapsed.TotalSeconds;
                        var averageScan = totalScanMilliseconds / Math.Max(frameCount, 1);
                        int radarCount;
                        int radarLocalCount;
                        ulong activeLocal;
                        lock (_sync)
                        {
                            radarCount = _radarItems.Count;
                            radarLocalCount = _radarItems.Count(x => x.IsLocal);
                        }
                        lock (_sourceSync) activeLocal = _activeLocalActor;
                        Volatile.Write(ref _readFps, scanFps);
                        Volatile.Write(ref _readAverageMs, averageScan);
                        LogMessage(2, $"ESP status: {frame.Status}; levels={frame.LevelCount}, levelActors={frame.LevelActorCount}, actors={frame.ActorCount}, playerStates={frame.PlayerStateCount}, playerPawns={frame.PlayerPawnCount}, localPawns={frame.LocalPawnCount}, globalVehicles={frame.GlobalVehicleCount}, players={frame.PlayerCount}, vehicles={frame.VehicleCount}, projected={frame.Items.Count}, radar={radarCount}, radarLocal={radarLocalCount}, activeLocal=0x{activeLocal:X}, scanFps={scanFps:0.0}, scanAvgMs={averageScan:0.00}, scanMaxMs={maxScanMilliseconds:0.00}; teams={TeamMetadataStatus}; skeleton={GetSkeletonDiagSummary()}; classes=[{classes}]");
                        frameCount = 0;
                        totalScanMilliseconds = 0;
                        maxScanMilliseconds = 0;
                        reportTimer.Restart();
                    }
                }
                catch (Exception ex)
                {
                    lock (_sync) _items = Array.Empty<EspItem>();
                    if (reportTimer.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        LogMessage(1, $"ESP scan error: {ex.GetType().Name}: {ex.Message}");
                        reportTimer.Restart();
                    }
                }
                finally { ExitDma(); }
                var readInterval = _settings.ReadIntervalMs;
                if (readInterval > 0)
                {
                    var delay = Math.Max(1, readInterval - (int)frameTimer.ElapsedMilliseconds);
                    if (_stop.Token.WaitHandle.WaitOne(delay)) break;
                }
                else
                {
                    Thread.Yield();
                }
            }
        }

        public EspScan ScanOnce()
        {
            var world = ReadPtr(_base + GWorld);
            if (world == 0) return EspScan.Empty("world unavailable");
            _cachedController = ReadLocalController();
            RefreshPlayerSources(world);
            RefreshLevelSources(world);
            RefreshGlobalVehicleActors();
            RefreshTargetIdentities(force: true);
            return ScanFrame();
        }

        public void DumpRuntimeChain(ulong world)
        {
            if (!IsPlausiblePointer(world))
            {
                Console.WriteLine("ESP chain: world=0");
                return;
            }

            static string P(ulong value) => value == 0 ? "0" : $"0x{value:X}";
            try
            {
                var persistentLevel = TryReadPtr(world + Offsets.WorldPersistentLevel);
                var levelsData = TryReadPtr(world + Offsets.WorldLevels);
                var levelsCount = ReadInt32(world + Offsets.WorldLevels + 8);
                var gameState = TryReadPtr(world + Offsets.WorldGameState);
                var gameInstance = TryReadPtr(world + Offsets.GameInstance);
                Console.WriteLine($"ESP chain: world={P(world)}, persistentLevel={P(persistentLevel)}, levelsData={P(levelsData)}, levelsCount={levelsCount}, gameState={P(gameState)}, gameInstance={P(gameInstance)}");
                var namePoolBase = _base + GNames;
                var poolWords = new List<string>();
                for (var i = 0; i < 8; i++)
                    poolWords.Add($"+0x{i * 8:X}=0x{ReadPtr(namePoolBase + (ulong)i * 8):X}");
                Console.WriteLine($"ESP namepool: {string.Join(", ", poolWords)}");
                Console.WriteLine($"ESP name probes: {ProbeNameId(0xC1F73)}, {ProbeNameId(0xBD95A)}, {ProbeNameId(0xBA85)}");

                if (persistentLevel != 0)
                {
                    var cluster = TryReadPtr(persistentLevel + Offsets.LevelActorContainer);
                    var actorsData = cluster == 0 ? 0 : TryReadPtr(cluster + Offsets.LevelActorsArray);
                    var actorsCount = cluster == 0 ? 0 : ReadInt32(cluster + Offsets.LevelActorsArray + 8);
                    Console.WriteLine($"ESP chain level: cluster={P(cluster)}, actorsData={P(actorsData)}, actorsCount={actorsCount}");
                    if (actorsData != 0 && actorsCount > 0 && actorsCount <= 32)
                    {
                        for (var i = 0; i < actorsCount; i++)
                        {
                            var actor = TryReadPtr(actorsData + (ulong)i * 8);
                            var cls = actor == 0 ? 0 : TryReadPtr(actor + Offsets.ActorClass);
                            var root = actor == 0 ? 0 : TryReadPtr(actor + Offsets.ActorRootComponent);
                            var name = cls == 0 ? string.Empty : ResolveClassNameForDiagnostic(cls);
                            Console.WriteLine($"ESP chain actor[{i}]: actor={P(actor)}, class={P(cls)}, root={P(root)}, name={name}");
                        }
                    }
                }

                if (gameState != 0)
                {
                    var playerArray = TryReadPtr(gameState + Offsets.GameStatePlayerArray);
                    var playerCount = ReadInt32(gameState + Offsets.GameStatePlayerArray + 8);
                    Console.WriteLine($"ESP chain players: playerArray={P(playerArray)}, playerCount={playerCount}");
                    if (playerArray != 0 && playerCount > 0 && playerCount <= 32)
                    {
                        for (var i = 0; i < playerCount; i++)
                        {
                            var state = TryReadPtr(playerArray + (ulong)i * 8);
                            var pawn = state == 0 ? 0 : TryReadPtr(state + Offsets.PlayerStatePawn);
                            var cls = pawn == 0 ? 0 : TryReadPtr(pawn + Offsets.ActorClass);
                            var root = pawn == 0 ? 0 : TryReadPtr(pawn + Offsets.ActorRootComponent);
                            var name = cls == 0 ? string.Empty : ResolveClassNameForDiagnostic(cls);
                            Console.WriteLine($"ESP chain player[{i}]: state={P(state)}, pawn={P(pawn)}, class={P(cls)}, root={P(root)}, name={name}");
                        }
                    }
                }

                if (gameInstance != 0)
                {
                    var localPlayers = TryReadPtr(gameInstance + Offsets.LocalPlayers);
                    var localPlayersCount = ReadInt32(gameInstance + Offsets.LocalPlayers + 8);
                    var localPlayer = localPlayers == 0 ? 0 : TryReadPtr(localPlayers);
                    var controller = localPlayer == 0 ? 0 : TryReadPtr(localPlayer + Offsets.LocalPlayerController);
                    var cameraManager = controller == 0 ? 0 : TryReadPtr(controller + Offsets.CameraManager);
                    Console.WriteLine($"ESP chain local: localPlayers={P(localPlayers)}, localPlayersCount={localPlayersCount}, localPlayer={P(localPlayer)}, controller={P(controller)}, cameraManager={P(cameraManager)}");
                    if (cameraManager != 0)
                    {
                        foreach (var cacheOffset in Offsets.CameraCacheCandidates)
                        {
                            try
                            {
                                var raw = ReadExact(_process, cameraManager + cacheOffset, 0x60);
                                var pov = (int)Offsets.CameraPov;
                                var x = BitConverter.ToDouble(raw, pov);
                                var y = BitConverter.ToDouble(raw, pov + 8);
                                var z = BitConverter.ToDouble(raw, pov + 16);
                                var pitch = BitConverter.ToDouble(raw, pov + 24);
                                var yaw = BitConverter.ToDouble(raw, pov + 32);
                                var roll = BitConverter.ToDouble(raw, pov + 40);
                                var fov = BitConverter.ToSingle(raw, pov + 48);
                                Console.WriteLine($"ESP chain camera +0x{cacheOffset:X}: loc=({x:0.0},{y:0.0},{z:0.0}), rot=({pitch:0.0},{yaw:0.0},{roll:0.0}), fov={fov:0.0}");
                            }
                            catch { Console.WriteLine($"ESP chain camera +0x{cacheOffset:X}: read-failed"); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ESP chain error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private string ResolveClassNameForDiagnostic(ulong cls)
        {
            try
            {
                var id = ReadUInt32(cls + Offsets.ClassNameId);
                return $"{_names.Resolve(id)}(id=0x{id:X})";
            }
            catch { return "<name-read-failed>"; }
        }

        private string ProbeNameId(uint id)
        {
            try
            {
                var pool = _base + GNames;
                var blockIndex = id >> 16;
                var offset = id & 0xFFFF;
                var block = ReadPtr(pool + (ulong)blockIndex * 8);
                var parts = new List<string>();
                foreach (var scale in new[] { 1, 2, 4, 8 })
                {
                    var at = block + (ulong)offset * (ulong)scale;
                    var bytes = ReadExact(_process, at, 24);
                    parts.Add($"x{scale}:{Convert.ToHexString(bytes.AsSpan(0, Math.Min(12, bytes.Length)))}");
                }
                return $"id=0x{id:X},block=0x{block:X},off=0x{offset:X}," + string.Join(";", parts);
            }
            catch (Exception ex) { return $"id=0x{id:X},error={ex.GetType().Name}"; }
        }

        private EspScan ScanFrame()
        {
            // Radar collection does not need a camera or viewport. When ESP
            // is disabled (or all ESP actor classes are hidden), avoid the
            // extra camera DMA and projection work entirely.
            var needsProjection = _settings.Enabled &&
                (_settings.ShowPlayers || _settings.ShowVehicles);
            var size = needsProjection ? GetViewport() : Size.Empty;
            var viewportValid = !needsProjection || (size.Width >= 100 && size.Height >= 100);
            // Camera data is only required by the screen ESP.  Reading it is
            // intentionally independent from radar collection so a transient
            // camera-cache miss cannot make the radar disappear.
            CameraState camera = default;
            var cameraValid = true;
            if (needsProjection) cameraValid = TryReadCamera(out camera);

            HashSet<ulong> actors;
            IReadOnlyList<ActorIdentity> identities;
            int levelCount, levelActorCount, playerStateCount, playerPawnCount, localPawnCount, globalVehicleCount;
            IReadOnlyList<string> sourceClassNames;
            Dictionary<ulong, PlayerVisualMeta> metadataSnapshot;
            ulong activeLocalActor;
            lock (_sourceSync)
            {
                actors = new HashSet<ulong>(_identityActors);
                identities = _targetIdentities;
                metadataSnapshot = new Dictionary<ulong, PlayerVisualMeta>(_playerMeta);
                levelCount = _levelCount;
                levelActorCount = _levelActorCount;
                playerStateCount = _playerStateCount;
                playerPawnCount = _playerPawnCount;
                localPawnCount = _localPawnCount;
                globalVehicleCount = _globalVehicleActors.Count;
                sourceClassNames = _sourceClassNames;
                activeLocalActor = _activeLocalActor;
            }

            if (identities.Count == 0)
            {
                lock (_sync) _radarItems = Array.Empty<RadarItem>();
                return EspScan.Empty("waiting for actor sources");
            }

            var result = new List<EspItem>();
            var radarResult = new List<RadarItem>(identities.Count);
            var playerCount = 0;
            var vehicleCount = 0;
            var actorData = ReadActorFrameData(identities);
            ReadCachedPoseBuffersBatch(actorData);
            // Keep skeleton cadence responsive; crowd optimization is handled
            // by the batched transform reads and thread priorities.
            _skeletonCacheWindowMs = 100;
            foreach (var data in actorData)
            {
                var info = GetActorClassInfo(data.Actor, data.Class);
                if (!info.IsPlayer && !info.IsVehicle) continue;
                var meta = default(PlayerVisualMeta);
                var hasMeta = metadataSnapshot.TryGetValue(data.Actor, out meta);
                // GenericTeamId is the authoritative relation in this game.
                // There are three teams in a match: only the local id is an
                // ally; every other *known* id is an enemy.  Faction object
                // pointers are per-player and must not be treated as team
                // ids, otherwise distant allies briefly render red.
                var teamKnown = hasMeta && _localTeam >= 0 && meta.Team >= 0;
                var factionKnown = hasMeta && !string.IsNullOrWhiteSpace(meta.FactionName);
                // Faction tags are stable across replication and identify the
                // three sides directly. GenericTeamId is a fallback only;
                // never compare per-object AWDFaction pointers.
                var sameFaction = factionKnown && !string.IsNullOrWhiteSpace(_localFactionName) &&
                                  string.Equals(meta.FactionName, _localFactionName, StringComparison.OrdinalIgnoreCase);
                // A replicated faction tag is the stable identity used by
                // the scoreboard. Prefer it whenever both sides have a
                // resolved tag; GenericTeamId is a fallback for actors whose
                // tag is not replicated yet. This avoids stale team bytes
                // briefly swapping friend/enemy colors until the scoreboard
                // UI happens to refresh replication.
                var factionRelationKnown = factionKnown && !string.IsNullOrWhiteSpace(_localFactionName);
                var friendly = factionRelationKnown ? sameFaction : teamKnown && meta.Team == _localTeam;
                var enemy = factionRelationKnown ? !sameFaction : teamKnown && meta.Team != _localTeam;
                var isLocal = data.Actor == activeLocalActor;
                var radarName = info.Name;
                if (info.IsPlayer)
                {
                    var state = friendly ? "ALLY" : enemy ? "ENEMY" : "UNKNOWN";
                    if (meta.Downed) state = "DOWNED";
                    radarName = string.IsNullOrWhiteSpace(meta.Name) ? info.Name : meta.Name;
                    var factionText = string.IsNullOrWhiteSpace(meta.FactionName) ? string.Empty : $" [{meta.FactionName}]";
                    radarName = $"{state} {radarName}{factionText}";
                }
                else if (hasMeta)
                {
                    var vehicleState = friendly ? "ALLY" : enemy ? "ENEMY" : "UNKNOWN";
                    var factionText = string.IsNullOrWhiteSpace(meta.FactionName) ? string.Empty : $" [{meta.FactionName}]";
                    radarName = $"{vehicleState} {info.Name}{factionText}";
                }
                // This list is intentionally populated before all ESP-only
                // settings and projection tests below.
                radarResult.Add(new RadarItem(data.Actor, data.Location, data.Yaw,
                    isLocal, info.IsPlayer, friendly, teamKnown, meta.Downed,
                    info.IsPlayer ? meta.Health : -1, radarName, data.Weapon));
                if (isLocal && float.IsFinite(data.Location.X) && float.IsFinite(data.Location.Y) && float.IsFinite(data.Location.Z))
                {
                    lock (_sourceSync)
                    {
                        _lastLocalWorldPosition = data.Location;
                        _lastLocalYaw = data.Yaw;
                        _hasLastLocalWorldPosition = true;
                    }
                }
                if (!_settings.Enabled) continue;
                if (info.IsPlayer)
                {
                    if (friendly && !_settings.ShowTeammates) continue;
                    if (enemy && !_settings.ShowEnemies) continue;
                    if (meta.Downed && !_settings.ShowDowned) continue;
                    if (!_settings.ShowPlayers) continue;
                }
                else
                {
                    if (friendly && !_settings.ShowFriendlyVehicles) continue;
                    if (enemy && !_settings.ShowEnemyVehicles) continue;
                    if (enemy && !_settings.ShowEnemies) continue;
                    if (!_settings.ShowVehicles) continue;
                }
                if (info.IsPlayer) playerCount++; else vehicleCount++;
                var distance = Vector3.Distance(camera.Location, data.Location) / 100.0;
                if (!cameraValid || !viewportValid) continue;
                if (distance > 20000 || !TryBuildBox(camera, data.Location, info.IsPlayer, meta.Downed, size,
                    out var box)) continue;
                // Keep off-screen targets in the ESP snapshot. Clamp their
                // box to the nearest viewport edge so they remain visible as
                // edge contacts instead of appearing only after turning the
                // camera toward them.
                if (box.Right < 0 || box.Bottom < 0 || box.Left > size.Width || box.Top > size.Height)
                {
                    if (info.IsPlayer) playerCount--; else vehicleCount--;
                    continue;
                }
                // Do not download a full animated pose for actors entirely
                // outside the viewport. Their box/name is still available at
                // the edge, but off-screen skeleton DMA was dominant in
                // crowded scenes. Distance gating also avoids spending a
                // complete pose read on tiny distant targets.
                var skeleton = info.IsPlayer && _settings.ShowSkeleton
                    ? BuildSkeleton(camera, data.Mesh, data.Location, size)
                    : Array.Empty<BoneLine>();
                var name = info.Name;
                if (info.IsPlayer)
                {
                    var state = friendly ? "ALLY" : enemy ? "ENEMY" : "UNKNOWN";
                    if (meta.Downed) state = "DOWNED";
                    var displayName = string.IsNullOrWhiteSpace(meta.Name) ? name : meta.Name;
                    var healthText = meta.Health >= 0 ? $" {meta.Health:0}%" : string.Empty;
                    var factionText = string.IsNullOrWhiteSpace(meta.FactionName) ? string.Empty : $" [{meta.FactionName}]";
                    name = $"{state} {displayName}{factionText}{healthText}";
                    if (_settings.ShowWeapons && !string.IsNullOrWhiteSpace(data.Weapon)) name += $"  [{data.Weapon}]";
                }
                else if (hasMeta)
                {
                    var vehicleState = friendly ? "ALLY" : enemy ? "ENEMY" : "UNKNOWN";
                    var factionText = string.IsNullOrWhiteSpace(meta.FactionName) ? string.Empty : $" [{meta.FactionName}]";
                    name = $"{vehicleState} {name}{factionText}";
                }
                result.Add(new EspItem(data.Actor, data.Location, data.Yaw, isLocal,
                    box.X, box.Y, box.Width, box.Height,
                    info.IsPlayer, friendly, teamKnown, meta.Downed,
                    info.IsPlayer ? meta.Health : -1, skeleton, name, distance, data.Weapon));
            }

            // Feed the next DMA prefilter from this completed frame. This is
            // a pure cache operation: no memory read is issued here.
            var nextSkeleton = new HashSet<ulong>(result.Where(x => x.IsPlayer &&
                Vector3.Distance(camera.Location, x.WorldPosition) <= 2500 &&
                x.Width > 1 && x.Height > 1).Select(x => x.Actor));
            lock (_skeletonPrefilterSync) _previousVisibleSkeletonActors = nextSkeleton;

            if (!radarResult.Any(item => item.IsLocal))
            {
                Vector3 fallbackLocation = default;
                float fallbackYaw = 0;
                // The camera follows the mortar emplacement immediately;
                // prefer it over the character cache, whose pawn may be
                // parked at the old body while the weapon view is active.
                var haveFallback = false;
                if (cameraValid)
                {
                    fallbackLocation = camera.Location;
                    fallbackYaw = camera.Rotation.Y;
                    haveFallback = float.IsFinite(fallbackLocation.X) &&
                                   float.IsFinite(fallbackLocation.Y) &&
                                   float.IsFinite(fallbackLocation.Z) &&
                                   fallbackLocation.Length() > 1 && fallbackLocation.Length() < 1e8f;
                }
                if (!haveFallback)
                    haveFallback = TryGetLastLocalWorld(out fallbackLocation, out fallbackYaw);
                if (haveFallback)
                {
                    radarResult.Add(new RadarItem(activeLocalActor, fallbackLocation, fallbackYaw,
                        true, true, true, false, false, -1, "LOCAL", string.Empty));
                }
            }

            if (result.Count > 0 && DateTime.UtcNow - _lastProjectionDiagUtc > TimeSpan.FromSeconds(2))
            {
                _lastProjectionDiagUtc = DateTime.UtcNow;
                var sample = result[0];
                LogMessage(3, $"ESP projection sample: x={sample.X:0.0},y={sample.Y:0.0},w={sample.Width:0.0},h={sample.Height:0.0},players={playerCount},vehicles={vehicleCount}");
            }

            lock (_sync) _radarItems = radarResult;
            if (!viewportValid) return EspScan.Empty("viewport unavailable");
            if (!cameraValid) return EspScan.Empty("camera unavailable");

            return new EspScan(result, levelCount, levelActorCount, actors.Count, playerStateCount,
                playerPawnCount, localPawnCount, globalVehicleCount, playerCount, vehicleCount,
                sourceClassNames, "OK");
        }

        private static bool TryBuildBox(CameraState camera, Vector3 center, bool isPlayer, bool downed,
            Size viewport, out RectangleF box)
        {
            // Project all corners of a small world-space bounds instead of
            // deriving width from the projected top/bottom line.  The latter
            // becomes unstable with a high-magnification optic: one endpoint
            // can leave the frustum and the averaged X value makes the box
            // appear to float away from the target.  Corner extents remain
            // stable and naturally follow perspective/ADS zoom.
            var hx = isPlayer ? (downed ? 90f : 35f) : 125f;
            var hy = isPlayer ? (downed ? 90f : 35f) : 125f;
            var hz = isPlayer ? (downed ? 38f : 90f) : 125f;
            var minX = float.PositiveInfinity; var minY = float.PositiveInfinity;
            var maxX = float.NegativeInfinity; var maxY = float.NegativeInfinity;
            var projected = 0;
            for (var mask = 0; mask < 8; mask++)
            {
                var world = center + new Vector3(
                    (mask & 1) == 0 ? -hx : hx,
                    (mask & 2) == 0 ? -hy : hy,
                    (mask & 4) == 0 ? -hz : hz);
                if (!Project(camera, world, viewport, out var p)) continue;
                projected++;
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
            }
            if (projected < 2 || !float.IsFinite(minX) || !float.IsFinite(minY) ||
                !float.IsFinite(maxX) || !float.IsFinite(maxY))
            {
                // At wide FOV or shallow viewing angles some valid targets
                // have only one visible bounds corner. Keep a small center
                // marker so they do not appear only after turning toward the
                // edge of the view.
                if (Project(camera, center, viewport, out var cp))
                {
                    var halfW = isPlayer ? 5f : 8f;
                    var halfH = isPlayer ? 10f : 8f;
                    box = new RectangleF(cp.X - halfW, cp.Y - halfH, halfW * 2, halfH * 2);
                    return true;
                }
                box = default;
                return false;
            }
            // Keep a broad guard for stale camera/actor data while allowing
            // legitimate off-screen extremities to be clipped by the overlay.
            var limitX = viewport.Width * 8f; var limitY = viewport.Height * 8f;
            minX = Math.Clamp(minX, -limitX, limitX); maxX = Math.Clamp(maxX, -limitX, limitX);
            minY = Math.Clamp(minY, -limitY, limitY); maxY = Math.Clamp(maxY, -limitY, limitY);
            var width = maxX - minX; var height = maxY - minY;
            if (!float.IsFinite(width) || !float.IsFinite(height) || width < 2f || height < 2f ||
                width > viewport.Width * 8f || height > viewport.Height * 8f)
            {
                if (Project(camera, center, viewport, out var cp))
                {
                    var halfW = isPlayer ? 5f : 8f;
                    var halfH = isPlayer ? 10f : 8f;
                    cp = new PointF(Math.Clamp(cp.X, 0f, viewport.Width),
                        Math.Clamp(cp.Y, 0f, viewport.Height));
                    box = new RectangleF(cp.X - halfW, cp.Y - halfH, halfW * 2, halfH * 2);
                    return true;
                }
                box = default;
                return false;
            }
            box = new RectangleF(minX, minY, width, height);
            return true;
        }

        // Resolve the currently equipped USkinnedAsset reference skeleton. The
        // asset pointer is the cache key; re-read every 20 seconds to survive
        // UObject recycling and immediately invalidate pose data on swaps.
        private DynamicBoneMap? GetDynamicBoneMap(ulong mesh)
        {
            if (!IsPlausiblePointer(mesh)) return null;
            ulong asset;
            var now = DateTime.UtcNow;
            if (_meshAssetCache.TryGetValue(mesh, out var cachedAsset) &&
                now - cachedAsset.Stamp < TimeSpan.FromMilliseconds(500))
            {
                asset = cachedAsset.Asset;
            }
            else
            {
                try { asset = ReadPtr(mesh + Offsets.SkinnedAsset); } catch { return null; }
            }
            if (!IsPlausiblePointer(asset))
            {
                if (_boneMapFailureDiag.TryAdd(mesh, 0)) LogMessage(3, $"ESP skeleton map miss: mesh=0x{mesh:X}, asset=0x{asset:X}");
                return null;
            }
            if (_meshAssetCache.TryGetValue(mesh, out var previous) && previous.Asset != asset)
            {
                _poseCache.TryRemove(mesh, out _); _skeletonCache.TryRemove(mesh, out _);
                _poseMissCache.TryRemove(mesh, out _);
            }
            _meshAssetCache[mesh] = (asset, now);
            if (_boneMapCache.TryGetValue(asset, out var cached) && now - cached.Stamp < TimeSpan.FromSeconds(20)) return cached.Map;
            DynamicBoneMap? map = ReadDynamicBoneMap(asset);
            if (map is null)
            {
                if (_boneMapFailureDiag.TryAdd(mesh, 0)) LogMessage(3, $"ESP skeleton map miss: mesh=0x{mesh:X}, asset=0x{asset:X}, ref-skeleton-invalid");
                return null;
            }
            _boneMapCache[asset] = (now, map);
            // A 20-second revalidation can observe a recycled asset at the
            // same address. Drop pose/skeleton snapshots so no old index map
            // survives the refresh.
            _poseCache.TryRemove(mesh, out _);
            _skeletonCache.TryRemove(mesh, out _);
            _poseMissCache.TryRemove(mesh, out _);
            LogMessage(3, $"ESP skeleton map: asset=0x{asset:X}, bones={map.Count}, resolved={map.Names.Count}, edges={map.Edges.Length}, samples={map.SampleIndices.Length}");
            return map;
        }

        private DynamicBoneMap? ReadDynamicBoneMap(ulong asset)
        {
            try
            {
                var h = ReadExact(_process, asset + Offsets.RawRefBoneInfo, 0x10);
                var raw = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0, 8));
                var count = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8, 4));
                if (!IsPlausiblePointer(raw) || count <= 0 || count > 1024)
                {
                    LogMessage(3, $"ESP skeleton map detail: asset=0x{asset:X}, raw=0x{raw:X}, refCount={count}");
                    return null;
                }
                var data = ReadExact(_process, raw, checked((uint)(count * Offsets.MeshBoneInfoSize)));
                var ids = new uint[count]; var parents = new int[count];
                for (var i = 0; i < count; i++)
                {
                    var at = i * Offsets.MeshBoneInfoSize;
                    ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));
                    parents[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 8, 4));
                    if (parents[i] < 0 || parents[i] >= count) parents[i] = -1;
                }
                var resolved = _names.ResolveBatch(ids);
                var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < count; i++)
                    if (resolved.TryGetValue(ids[i], out var n) && !string.IsNullOrWhiteSpace(n))
                    { var key = NormalizeBoneName(n); if (!names.ContainsKey(key)) names[key] = i; }
                var selected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in BoneAliases)
                { var index = FindBone(names, pair.Value); if (index >= 0) selected[pair.Key] = index; }
                var edges = new List<(int A, int B)>();
                AddChain(edges, selected, "pelvis", "spine_01", "spine_02", "spine_03", "spine_04", "spine_05", "neck", "head");
                AddChain(edges, selected, "spine_05", "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l");
                AddChain(edges, selected, "spine_05", "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r");
                AddChain(edges, selected, "pelvis", "thigh_l", "calf_l", "foot_l");
                AddChain(edges, selected, "pelvis", "thigh_r", "calf_r", "foot_r");
                var samples = selected.Values.Distinct().ToArray();
                if (samples.Length < 4 || edges.Count < 3)
                {
                    var sampleNames = string.Join(";", ids.Take(12).Select(id => $"{id:X8}:{(resolved.TryGetValue(id, out var rv) ? rv : "?")}"));
                    LogMessage(3, $"ESP skeleton map detail: asset=0x{asset:X}, refCount={count}, names={names.Count}, selected={selected.Count}, edges={edges.Count}, keys={string.Join(',', names.Keys.Take(12))}, sample={sampleNames}");
                    return null;
                }
                return new DynamicBoneMap(asset, count, parents, selected, edges.Distinct().ToArray(), samples);
            }
            catch { return null; }
        }

        private static readonly Dictionary<string, string[]> BoneAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["pelvis"] = new[] { "pelvis", "hip" }, ["spine_01"] = new[] { "spine_01", "spine01", "spine_1" },
            ["spine_02"] = new[] { "spine_02", "spine02", "spine_2" }, ["spine_03"] = new[] { "spine_03", "spine03", "spine_3" },
            ["spine_04"] = new[] { "spine_04", "spine04", "spine_4" }, ["spine_05"] = new[] { "spine_05", "spine05", "spine_5" },
            ["neck"] = new[] { "neck_01", "neck01", "neck" }, ["head"] = new[] { "head", "head_end" },
            ["clavicle_l"] = new[] { "clavicle_l", "clavicleleft" }, ["upperarm_l"] = new[] { "upperarm_l", "upper_arm_l", "upperarmleft" },
            ["lowerarm_l"] = new[] { "lowerarm_l", "lower_arm_l", "forearm_l", "lowerarmleft" }, ["hand_l"] = new[] { "hand_l", "handleft" },
            ["clavicle_r"] = new[] { "clavicle_r", "clavicleright" }, ["upperarm_r"] = new[] { "upperarm_r", "upper_arm_r", "upperarmright" },
            ["lowerarm_r"] = new[] { "lowerarm_r", "lower_arm_r", "forearm_r", "lowerarmright" }, ["hand_r"] = new[] { "hand_r", "handright" },
            ["thigh_l"] = new[] { "thigh_l", "thighleft" }, ["calf_l"] = new[] { "calf_l", "calfleft" }, ["foot_l"] = new[] { "foot_l", "ball_l", "footleft" },
            ["thigh_r"] = new[] { "thigh_r", "thighright" }, ["calf_r"] = new[] { "calf_r", "calfright" }, ["foot_r"] = new[] { "foot_r", "ball_r", "footright" }
        };

        private static string NormalizeBoneName(string value)
        {
            var s = value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            var colon = s.LastIndexOf(':'); if (colon >= 0 && colon + 1 < s.Length) s = s[(colon + 1)..];
            while (s.StartsWith("b_", StringComparison.Ordinal)) s = s[2..];
            return s;
        }
        private static int FindBone(Dictionary<string, int> names, string[] aliases)
        {
            foreach (var a in aliases) if (names.TryGetValue(a, out var i)) return i;
            foreach (var p in names) foreach (var a in aliases)
                if (p.Key.EndsWith("_" + a, StringComparison.OrdinalIgnoreCase) || p.Key.EndsWith(a, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return -1;
        }
        private static void AddChain(List<(int A, int B)> edges, Dictionary<string, int> selected, params string[] chain)
        {
            var previous = -1; foreach (var name in chain) if (selected.TryGetValue(name, out var current))
            { if (previous >= 0 && previous != current) edges.Add((previous, current)); previous = current; }
        }

        private BoneLine[] BuildSkeleton(CameraState camera, ulong mesh, Vector3 center, Size viewport)
        {
            Interlocked.Increment(ref _skeletonCalls);
            if (!IsPlausiblePointer(mesh))
            {
                RecordSkeletonDiag("mesh-invalid", mesh, 0, 0, 0);
                return Array.Empty<BoneLine>();
            }
            try
            {
                var boneMap = GetDynamicBoneMap(mesh);
                if (boneMap is null)
                {
                    Interlocked.Increment(ref _skeletonFailures);
                    RecordSkeletonDiag("ref-skeleton-miss", mesh, 0, 0, 0);
                    return Array.Empty<BoneLine>();
                }
                if (_poseMissCache.TryGetValue(mesh, out var miss) &&
                    DateTime.UtcNow - miss < TimeSpan.FromSeconds(1))
                {
                    Interlocked.Increment(ref _skeletonFailures);
                    RecordSkeletonDiag("pose-miss-cache", mesh, 0, 0, 0);
                    return Array.Empty<BoneLine>();
                }
                Dictionary<int, Vector3> positions;
                ComponentTransform component;
                ulong selectedPose = 0;
                int selectedCount = 0, selectedStride = (int)Offsets.FTransformSize;
                int selectedTranslation = (int)Offsets.FTransformTranslation;
                ulong selectedOffset = 0;
                if (_skeletonCache.TryGetValue(mesh, out var cached) &&
                    // Pose extraction downloads the complete transform array.
                    // Keep an adaptive pose cache: root positions/camera still
                    // update every scan, while skeleton DMA is capped more
                    // aggressively in crowded matches.
                    DateTime.UtcNow - cached.Stamp < TimeSpan.FromMilliseconds(_skeletonCacheWindowMs))
                {
                    Interlocked.Increment(ref _skeletonCacheHits);
                        selectedCount = cached.Count;
                    positions = cached.Relative;
                    component = cached.Component;
                }
                else
                {
                    if (_poseCache.TryGetValue(mesh, out var known) &&
                        TryReadPoseBuffer(known.Pose, known.Count, known.Stride, known.Translation,
                            IsLocalPoseOffset(known.Offset), known.Composed, boneMap.SampleIndices, boneMap.Parents, out positions, out _))
                    {
                        selectedPose = known.Pose; selectedCount = known.Count;
                        selectedStride = known.Stride; selectedTranslation = known.Translation;
                        selectedOffset = known.Offset;
                        component = ReadMeshComponentTransform(mesh, center);
                    }
                    else if (!TrySelectBonePose(mesh, out var pose, out var count,
                        out var stride, out var translation, out var poseOffset,
                        boneMap.SampleIndices, boneMap.Parents, out positions, out var usedComposed, out var report))
                    {
                        _poseMissCache[mesh] = DateTime.UtcNow;
                        Interlocked.Increment(ref _skeletonFailures);
                        RecordSkeletonDiag($"pose-not-found[{report}]", mesh, 0, 0, 0);
                        return Array.Empty<BoneLine>();
                    }
                    else
                    {
                        selectedPose = pose; selectedCount = count;
                        selectedOffset = poseOffset;
                        selectedStride = stride; selectedTranslation = translation;
                        _poseCache[mesh] = (pose, count, stride, translation, poseOffset, usedComposed);
                        component = ReadMeshComponentTransform(mesh, center);
                    }
                    // Some Wardogs meshes expose already-world-space transforms.
                    // Applying ComponentToWorld twice moves the rig off-screen, so
                    // select the representation by comparing it with the actor root.
                    var average = positions.Values.Aggregate(Vector3.Zero, (a, v) => a + v) /
                                  Math.Max(positions.Count, 1);
                    if (component.Valid && Vector3.Distance(average, center) < 600.0f)
                        component = new ComponentTransform(Quaternion.Identity, Vector3.Zero, Vector3.One, false);
                    _skeletonCache[mesh] = (DateTime.UtcNow, selectedCount, positions, component);
                }

            // FModel confirms that Wardogs ships two compatible player rigs:
            // the refactored/mutable mesh (177+ bones) and the legacy
            // SK_TPCharacter mesh (162 bones). Their first spine is identical,
            // but hand/leg indices differ after the corrective bones. Select
            // the edge table from the evaluated pose count rather than drawing
            // legacy indices on a refactored mesh. This also keeps both arms
            // visible (the old table accidentally omitted the left arm).
            var edges = boneMap.Edges;
            if (selectedCount <= 0 || boneMap.SampleIndices.Any(i => i < 0 || i >= selectedCount) ||
                edges.Any(e => e.A < 0 || e.B < 0 || e.A >= selectedCount || e.B >= selectedCount))
            {
                Interlocked.Increment(ref _skeletonFailures);
                RecordSkeletonDiag($"index-out-of-range-map{boneMap.Count}/pose{selectedCount}", mesh, selectedPose, selectedCount, 0);
                return Array.Empty<BoneLine>();
            }
            var output = new List<BoneLine>(edges.Length);
            foreach (var edge in edges)
            {
                if (!positions.TryGetValue(edge.A, out var pa) || !positions.TryGetValue(edge.B, out var pb) ||
                    !Project(camera, TransformPoint(pa, component), viewport, out var a) ||
                    !Project(camera, TransformPoint(pb, component), viewport, out var b)) continue;
                // A stale/partially streamed transform can project one endpoint
                // across the entire screen. Drop that segment instead of
                // producing the characteristic long horizontal/vertical bar.
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var span = MathF.Sqrt(dx * dx + dy * dy);
                if (!float.IsFinite(span) || span > MathF.Max(viewport.Width, viewport.Height) * 1.25f)
                    continue;
                output.Add(new BoneLine(a, b));
            }
            var lines = output.ToArray();
            if (lines.Length > 0) Interlocked.Increment(ref _skeletonSuccesses);
            else Interlocked.Increment(ref _skeletonFailures);
            var projectionReason = lines.Length > 0
                ? $"ok-{(component.Valid ? "ctw" : "center")}-o{selectedOffset:X}-s{selectedStride:X}-t{selectedTranslation:X}-{FormatCorePose(positions)}"
                : "projection-empty";
            RecordSkeletonDiag(projectionReason, mesh, selectedPose, selectedCount == 0 ? positions.Count : selectedCount, lines.Length);
            return lines;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _skeletonFailures);
                RecordSkeletonDiag($"exception-{ex.GetType().Name}", mesh, 0, 0, 0);
                return Array.Empty<BoneLine>();
            }
        }

        private static string FormatCorePose(Dictionary<int, Vector3> positions)
        {
            static string F(Dictionary<int, Vector3> p, int i) =>
                p.TryGetValue(i, out var v) ? $"{v.X:0},{v.Y:0},{v.Z:0}" : "-";
            return $"p1={F(positions, 1)};n7={F(positions, 7)};h9={F(positions, 9)}";
        }

        private static bool IsLocalPoseOffset(ulong offset) =>
            offset == Offsets.BoneArray || offset == Offsets.BoneArrayCache || offset == 0xA08;

        private bool TryReadPoseBuffer(ulong pose, int count, int stride, int translation,
            bool localPose, bool? forcedComposed, int[] indices, int[] parents, out Dictionary<int, Vector3> positions,
            out bool usedComposed)
        {
            positions = new Dictionary<int, Vector3>();
            usedComposed = false;
            // Keep the layout selected by TrySelectBonePose.  Different
            // Wardogs mesh generations expose either FTransform3d (0x60)
            // or FTransform3f (0x30); forcing one collapses valid poses.
            if (stride != 0x30 && stride != 0x60)
            {
                stride = (int)Offsets.FTransformSize;
                translation = (int)Offsets.FTransformTranslation;
            }
            if (!IsPlausiblePointer(pose) || count < 4 || count > 1024 ||
                stride < 0x20 || stride > 0x80 || translation < 0 || translation > stride - 12)
                return false;
            try
            {
                var size = checked((uint)(count * stride + translation + 12));
                byte[] bytes;
                if (!_framePoseBuffers.TryGetValue(pose, out bytes!) || bytes.Length < size)
                    bytes = ReadExact(_process, pose, size);
                var raw = ExtractBonePositions(bytes, count, indices, stride, translation);
                positions = raw;
                if (localPose && (stride == 0x30 || stride == 0x60))
                {
                    var composed = BuildComponentSpacePositions(bytes, count, stride, translation, indices, parents);
                    // Once a mesh has selected raw or parent-composed space,
                    // keep that representation stable until the pose pointer
                    // changes. Equipment/armor can temporarily alter the
                    // geometry score; re-selecting every frame makes the rig
                    // visibly jump between two coordinate spaces.
                    if (forcedComposed.HasValue)
                    {
                        usedComposed = forcedComposed.Value;
                        positions = usedComposed ? composed : raw;
                        if (ScoreBonePositions(positions) < 12 || positions.Count < 8)
                        {
                            var alternate = usedComposed ? raw : composed;
                            if (ScoreBonePositions(alternate) >= 12 && alternate.Count >= 8)
                                positions = alternate;
                        }
                    }
                    else if (ScoreBonePositions(composed) > ScoreBonePositions(raw))
                    {
                        positions = composed;
                        usedComposed = true;
                    }
                }
                return ScoreBonePositions(positions) >= 12 && positions.Count >= 8;
            }
            catch { positions = new Dictionary<int, Vector3>(); return false; }
        }

        private bool TrySelectBonePose(ulong mesh, out ulong pose, out int count,
            out int stride, out int translation, out ulong poseOffset,
            int[] indices, int[] parents, out Dictionary<int, Vector3> positions, out bool usedComposed,
            out string report)
        {
            pose = 0; count = 0; stride = (int)Offsets.FTransformSize;
            translation = (int)Offsets.FTransformTranslation;
            poseOffset = 0;
            positions = new Dictionary<int, Vector3>();
            usedComposed = false;
            var bestScore = -1;
            var reportBuilder = new StringBuilder(256);
            foreach (var offset in Offsets.PoseArrayCandidates)
            {
                byte[] header;
                try { header = ReadExact(_process, mesh + offset, 0x10); }
                catch { reportBuilder.Append($"+{offset:X}:readerr;"); continue; }
                var ptr = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0, 8));
                var rawCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
                var direct = offset == Offsets.BoneArray || offset == Offsets.BoneArrayCache;
                var candidateCount = direct && (rawCount < 4 || rawCount > 1024)
                    ? Offsets.DirectBoneArrayFallbackCount : rawCount;
                if (!IsPlausiblePointer(ptr) || candidateCount < 4 || candidateCount > 1024)
                {
                    reportBuilder.Append($"+{offset:X}=0x{ptr:X}/{rawCount};");
                    continue;
                }
                // Read a single block and evaluate the possible compact
                // transform layouts. This also rejects unrelated pointers at
                // +0x620/+0x630 which previously yielded a five-line rig.
                byte[] bytes;
                try
                {
                    // Read the complete candidate. Refactored character rigs
                    // contain 356 transforms; sampling only 148 can make a
                    // local pose collapse to a pelvis point. If the target
                    // exposes the legacy 0x30 array at the end of a mapped
                    // region, retry the smaller span instead of discarding it.
                    try
                    {
                        bytes = ReadExact(_process, ptr, checked((uint)(candidateCount * 0x60)));
                    }
                    catch
                    {
                        bytes = ReadExact(_process, ptr, checked((uint)(candidateCount * 0x30)));
                    }
                }
                catch { reportBuilder.Append($"+{offset:X}=0x{ptr:X}/{candidateCount}:readerr;"); continue; }
                var candidateBest = new Dictionary<int, Vector3>();
                var candidateStride = 0; var candidateTranslation = 0; var candidateScore = -1;
                var candidateUsedComposed = false;
                Dictionary<int, Vector3>? canonicalBest = null;
                var canonicalScore = -1;
                foreach (var testStride in new[] { 0x60, 0x30, 0x40, 0x20 })
                foreach (var testTranslation in new[] { 0x20, 0x10, 0x00 })
                {
                    var sample = ExtractBonePositions(bytes, candidateCount, indices, testStride, testTranslation);
                    var score = ScoreBoneLayout(bytes, candidateCount, indices,
                        testStride, testTranslation, sample);
                    if (score > candidateScore)
                    {
                        candidateScore = score; candidateBest = sample;
                        candidateStride = testStride; candidateTranslation = testTranslation;
                    }
                    if (testStride == 0x60 && testTranslation == 0x20 &&
                        sample.Count >= 8 && ScoreBonePositions(sample) >= 12)
                    {
                        canonicalBest = sample;
                        canonicalScore = score;
                    }
                }
                // Prefer the documented UE FTransform layout whenever it
                // passes the geometry checks; random bytes at a 0x40 stride
                // otherwise win the heuristic and result in a static rig.
                if (canonicalBest is not null && canonicalScore >= candidateScore)
                {
                    candidateBest = canonicalBest;
                    candidateStride = 0x60;
                    candidateTranslation = 0x20;
                    candidateScore = canonicalScore;
                }
                var raw60 = ExtractBonePositions(bytes, candidateCount, indices, 0x60, 0x20);
                var raw30 = ExtractBonePositions(bytes, candidateCount, indices, 0x30, 0x10);
                reportBuilder.Append($"raw60={FormatCorePose(raw60)};raw30={FormatCorePose(raw30)};");
                if (IsLocalPoseOffset(offset) &&
                    (candidateStride == 0x30 || candidateStride == 0x60))
                {
                    // Custom +0x620/+0x630 arrays are not guaranteed to be
                    // local-space in every mesh generation. Compare raw and
                    // parent-composed geometry and retain the plausible one.
                    var raw = candidateBest;
                    var composed = BuildComponentSpacePositions(bytes, candidateCount,
                        candidateStride, candidateTranslation, indices, parents);
                    if (ScoreBonePositions(composed) > ScoreBonePositions(raw))
                    {
                        candidateBest = composed;
                        candidateUsedComposed = true;
                        candidateScore = ScoreBonePositions(composed) + 12;
                    }
                }
                // FTransform3d in this UE5/LWC build is 0x60 bytes (quat @0,
                // translation @0x20, scale @0x40). Keep compact float layouts
                // only as a compatibility fallback for older meshes.
                if (candidateStride == 0x40 && candidateScore < 32)
                    candidateScore -= 20;
                reportBuilder.Append($"+{offset:X}=0x{ptr:X}/{candidateCount}:s{candidateScore}/st{candidateStride:X}/tr{candidateTranslation:X}/p{candidateBest.Count};");
                if (candidateScore > bestScore)
                {
                    bestScore = candidateScore; pose = ptr; count = candidateCount;
                    stride = candidateStride; translation = candidateTranslation;
                    poseOffset = offset; positions = candidateBest;
                    usedComposed = candidateUsedComposed;
                }
            }
            report = reportBuilder.ToString();
            // A partially streamed mesh can temporarily miss one of the
            // named core indices.  Keep the pose when enough finite samples
            // exist; edge drawing will simply skip missing endpoints and the
            // next frame can recover the complete rig.
            return pose != 0 && bestScore >= 12 && positions.Count >= 8;
        }

        private static Dictionary<int, Vector3> ExtractBonePositions(byte[] bytes, int count,
            int[] indices, int stride, int translation)
        {
            var result = new Dictionary<int, Vector3>();
            foreach (var index in indices)
            {
                if (index >= count) continue;
                var at = checked(index * stride + translation);
                if (at < 0 || at + 12 > bytes.Length) continue;
                var lwc = stride >= 0x60;
                var x = lwc ? (float)BitConverter.ToDouble(bytes, at) : BitConverter.ToSingle(bytes, at);
                var y = lwc ? (float)BitConverter.ToDouble(bytes, at + 8) : BitConverter.ToSingle(bytes, at + 4);
                var z = lwc ? (float)BitConverter.ToDouble(bytes, at + 16) : BitConverter.ToSingle(bytes, at + 8);
                if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
                    MathF.Abs(x) < 10000 && MathF.Abs(y) < 10000 && MathF.Abs(z) < 10000)
                    result[index] = new Vector3(x, y, z);
            }
            return result;
        }

        private readonly record struct BoneTransform(Quaternion Rotation, Vector3 Translation, Vector3 Scale);

        private static Dictionary<int, Vector3> BuildComponentSpacePositions(byte[] bytes,
            int count, int stride, int translation, int[] indices, int[] parents)
        {
            var limit = Math.Min(count, parents.Length);
            var local = new BoneTransform[limit];
            var component = new BoneTransform[limit];
            var valid = new bool[limit];
            var lwc = stride >= 0x60;
            var transformSize = lwc ? 0x60 : 0x30;
            for (var i = 0; i < limit; i++)
            {
                var at = checked(i * stride);
                if (at < 0 || at + transformSize > bytes.Length || translation < 0 ||
                    translation + (lwc ? 24 : 12) > stride) continue;
                var q = lwc
                    ? new Quaternion((float)BitConverter.ToDouble(bytes, at), (float)BitConverter.ToDouble(bytes, at + 8),
                        (float)BitConverter.ToDouble(bytes, at + 16), (float)BitConverter.ToDouble(bytes, at + 24))
                    : new Quaternion(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4),
                        BitConverter.ToSingle(bytes, at + 8), BitConverter.ToSingle(bytes, at + 12));
                var t = lwc
                    ? new Vector3((float)BitConverter.ToDouble(bytes, at + translation),
                        (float)BitConverter.ToDouble(bytes, at + translation + 8),
                        (float)BitConverter.ToDouble(bytes, at + translation + 16))
                    : new Vector3(BitConverter.ToSingle(bytes, at + translation),
                        BitConverter.ToSingle(bytes, at + translation + 4), BitConverter.ToSingle(bytes, at + translation + 8));
                var s = lwc
                    ? new Vector3((float)BitConverter.ToDouble(bytes, at + 0x40),
                        (float)BitConverter.ToDouble(bytes, at + 0x48), (float)BitConverter.ToDouble(bytes, at + 0x50))
                    : new Vector3(BitConverter.ToSingle(bytes, at + 0x20),
                        BitConverter.ToSingle(bytes, at + 0x24), BitConverter.ToSingle(bytes, at + 0x28));
                var qLen = q.LengthSquared();
                if (!float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z) || !float.IsFinite(q.W) ||
                    !float.IsFinite(t.X) || !float.IsFinite(t.Y) || !float.IsFinite(t.Z) ||
                    !float.IsFinite(s.X) || !float.IsFinite(s.Y) || !float.IsFinite(s.Z) ||
                    qLen < 0.25f || qLen > 4.0f || MathF.Abs(t.X) > 10000 ||
                    MathF.Abs(t.Y) > 10000 || MathF.Abs(t.Z) > 10000) continue;
                local[i] = new BoneTransform(Quaternion.Normalize(q), t, s);
                valid[i] = true;
            }
            for (var i = 0; i < limit; i++)
            {
                if (!valid[i]) continue;
                var parent = i < parents.Length ? parents[i] : -1;
                component[i] = parent >= 0 && parent < i && valid[parent]
                    ? Compose(local[i], component[parent]) : local[i];
            }
            var output = new Dictionary<int, Vector3>(indices.Length);
            foreach (var index in indices)
                if (index >= 0 && index < limit && valid[index]) output[index] = component[index].Translation;
            return output;
        }
        private static BoneTransform Compose(BoneTransform child, BoneTransform parent)
        {
            // UE FTransform multiplication is parent * child.
            var rotation = Quaternion.Normalize(parent.Rotation * child.Rotation);
            var scale = parent.Scale * child.Scale;
            var translation = Vector3.Transform(child.Translation * parent.Scale, parent.Rotation) + parent.Translation;
            return new BoneTransform(rotation, translation, scale);
        }

        private static int ScoreBonePositions(Dictionary<int, Vector3> p)
        {
            if (p.Count == 0) return 0;
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            foreach (var v in p.Values)
            {
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) continue;
                min = Vector3.Min(min, v); max = Vector3.Max(max, v);
            }
            var extent = max - min;
            var score = p.Count;
            if (float.IsFinite(extent.X) && float.IsFinite(extent.Y) && float.IsFinite(extent.Z))
            {
                if (extent.Z > 15 && extent.Z < 500) score += 8;
                if (extent.X < 500 && extent.Y < 500) score += 4;
                if (extent.X > 1 && extent.Y > 1) score += 2;
            }
            var finiteDistances = 0;
            var values = p.Values.ToArray();
            for (var i = 0; i < values.Length && finiteDistances < 32; i++)
                for (var j = i + 1; j < values.Length && finiteDistances < 32; j++)
                {
                    var d = Vector3.Distance(values[i], values[j]);
                    if (float.IsFinite(d) && d > 1 && d < 500) finiteDistances++;
                }
            return score + Math.Min(finiteDistances, 12);
        }

        private void ReadCachedPoseBuffersBatch(IReadOnlyList<ActorFrameData> actorData)
        {
            var poseStarted = Stopwatch.GetTimestamp();
            _framePoseBuffers.Clear();
            if (!_settings.ShowSkeleton) { _lastPoseBatchMs = 0; return; }
            HashSet<ulong> visibleActors;
            lock (_skeletonPrefilterSync) visibleActors = new HashSet<ulong>(_previousVisibleSkeletonActors);
            var requests = new List<(ulong Pose, uint Size)>();
            foreach (var row in actorData)
            {
                if (!visibleActors.Contains(row.Actor) || row.Mesh == 0) continue;
                if (!_poseCache.TryGetValue(row.Mesh, out var pose) ||
                    !IsPlausiblePointer(pose.Pose) || pose.Count < 4 || pose.Count > 1024) continue;
                var size = checked((uint)(pose.Count * pose.Stride + pose.Translation + 12));
                if (size == 0 || size > 0x20000) continue;
                requests.Add((pose.Pose, size));
            }
            requests = requests.DistinctBy(x => x.Pose).ToList();
            if (requests.Count == 0) { _lastPoseBatchMs = 0; return; }
            var started = Stopwatch.GetTimestamp();
            // Keep each pose transaction bounded. One giant request for all
            // visible meshes can monopolize the FPGA queue and starve the
            // root-transform frame read; two small batches are enough to
            // restore the skeleton cache without creating a long frame tail.
            const int batchSize = 32;
            for (var start = 0; start < requests.Count; start += batchSize)
            {
                var length = Math.Min(batchSize, requests.Count - start);
                using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                for (var i = 0; i < length; i++)
                    scatter.Prepare(requests[start + i].Pose, requests[start + i].Size);
                if (!scatter.Execute()) continue;
                for (var i = 0; i < length; i++)
                {
                    var request = requests[start + i];
                    var data = scatter.Read(request.Pose, request.Size);
                    if (data is byte[] bytes && bytes.Length == request.Size)
                        _framePoseBuffers[request.Pose] = bytes;
                }
            }
            _lastScatterMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _lastPoseBatchMs = Stopwatch.GetElapsedTime(poseStarted).TotalMilliseconds;
        }

        private void RunDmaBenchmark()
        {
            try
            {
                ActorIdentity[] identities;
                lock (_sourceSync) identities = _targetIdentities.Take(256).ToArray();
                var addresses = identities.SelectMany(x => new[]
                {
                    (Address: x.Root + Offsets.SceneRelativeLocation, Size: 24u),
                    (Address: x.Root + Offsets.SceneRelativeRotation, Size: 24u)
                }).ToArray();
                if (addresses.Length == 0)
                {
                    lock (_dmaBenchmarkSync) _dmaBenchmarkStatus = "等待实体地址";
                    return;
                }
                var sgStart = Stopwatch.GetTimestamp();
                using (var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE))
                {
                    foreach (var request in addresses) scatter.Prepare(request.Address, request.Size);
                    scatter.Execute();
                }
                var sgMs = Stopwatch.GetElapsedTime(sgStart).TotalMilliseconds;
                var sequential = addresses.Take(Math.Min(64, addresses.Length)).ToArray();
                var seqStart = Stopwatch.GetTimestamp();
                var completed = 0;
                foreach (var request in sequential)
                {
                    var data = _process.MemRead(request.Address, request.Size, Vmm.FLAG_NOCACHE);
                    if (data is { Length: 24 }) completed++;
                }
                var seqMs = Stopwatch.GetElapsedTime(seqStart).TotalMilliseconds;
                lock (_dmaBenchmarkSync)
                    _dmaBenchmarkStatus = $"SG {addresses.Length}地址={sgMs:0.00}ms; 顺序 {sequential.Length}地址={seqMs:0.00}ms; 成功={completed}; 比值={(sgMs > 0 ? seqMs / sgMs : 0):0.0}x";
                LogMessage(2, $"DMA benchmark: sgAddresses={addresses.Length}, sgMs={sgMs:0.00}, sequentialAddresses={sequential.Length}, sequentialMs={seqMs:0.00}, completed={completed}");
            }
            catch (Exception ex)
            {
                lock (_dmaBenchmarkSync) _dmaBenchmarkStatus = $"失败: {ex.GetType().Name}: {ex.Message}";
                LogMessage(1, $"DMA benchmark error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static int ScoreBoneLayout(byte[] bytes, int count, int[] indices,
            int stride, int translation, Dictionary<int, Vector3> positions)
        {
            var score = ScoreBonePositions(positions);
            // A real FTransform starts with a unit quaternion.  Checking the
            // rotation slot prevents unrelated pointers from winning merely
            // because their bytes decode to finite FVector values.
            var quaternionSamples = 0;
            foreach (var index in indices)
            {
                if (index >= count) continue;
                var at = index * stride;
                if (at < 0 || at + 16 > bytes.Length) continue;
                var q = stride >= 0x60
                    ? new Quaternion((float)BitConverter.ToDouble(bytes, at), (float)BitConverter.ToDouble(bytes, at + 8),
                        (float)BitConverter.ToDouble(bytes, at + 16), (float)BitConverter.ToDouble(bytes, at + 24))
                    : new Quaternion(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4),
                        BitConverter.ToSingle(bytes, at + 8), BitConverter.ToSingle(bytes, at + 12));
                var qLen = q.LengthSquared();
                if (float.IsFinite(qLen) && qLen > 0.5f && qLen < 1.5f)
                    quaternionSamples++;
            }
            score += quaternionSamples * 2;
            if (quaternionSamples >= 8) score += 8;
            // The compact direct buffer is known to contain the full player
            // rig; reject layouts that only produce a handful of bones.
            if (positions.Count >= 12) score += 6;
            // Keep both UE5 transform generations available.  A 0x30 stride
            // is the legacy float layout; 0x60 is the LWC/double layout.
            // Penalize only the less common compact layouts.
            if (stride == 0x20) score -= 12;
            else if (stride == 0x40) score -= 4;
            return score;
        }

        private void RecordSkeletonDiag(string reason, ulong mesh, ulong pose, int count, int lines)
        {
            lock (_skeletonDiagSync)
            {
                _lastSkeletonDiag = $"reason={reason},mesh=0x{mesh:X},pose=0x{pose:X},count={count},lines={lines}";
                if (DateTime.UtcNow - _lastSkeletonDiagUtc < TimeSpan.FromSeconds(2)) return;
                _lastSkeletonDiagUtc = DateTime.UtcNow;
                LogMessage(3, $"ESP skeleton: calls={Interlocked.Read(ref _skeletonCalls)}, success={Interlocked.Read(ref _skeletonSuccesses)}, failures={Interlocked.Read(ref _skeletonFailures)}, cacheHits={Interlocked.Read(ref _skeletonCacheHits)}; last={_lastSkeletonDiag}");
            }
        }

        private ComponentTransform ReadMeshComponentTransform(ulong mesh, Vector3 fallbackLocation)
        {
            try
            {
                if (_componentTransformOffsetCache.TryGetValue(mesh, out var cached))
                {
                    var bytes = ReadExact(_process, mesh + cached, 0x60);
                    if (TryParseComponentTransform(bytes, out var hit)) return hit;
                    _componentTransformOffsetCache.TryRemove(mesh, out _);
                }
                foreach (var offset in Offsets.ComponentToWorldCandidates)
                {
                    var bytes = ReadExact(_process, mesh + offset, 0x60);
                    if (!TryParseComponentTransform(bytes, out var hit)) continue;
                    _componentTransformOffsetCache[mesh] = offset;
                    return hit;
                }
            }
            catch { }
            return new ComponentTransform(Quaternion.Identity, fallbackLocation, Vector3.One, false);
        }

        private static bool TryParseComponentTransform(byte[] bytes, out ComponentTransform transform)
        {
            transform = default;
            if (bytes.Length < 0x58) return false;
            try
            {
                var q = new Quaternion((float)BitConverter.ToDouble(bytes, 0),
                    (float)BitConverter.ToDouble(bytes, 8), (float)BitConverter.ToDouble(bytes, 16),
                    (float)BitConverter.ToDouble(bytes, 24));
                var t = new Vector3((float)BitConverter.ToDouble(bytes, 0x20),
                    (float)BitConverter.ToDouble(bytes, 0x28), (float)BitConverter.ToDouble(bytes, 0x30));
                var s = new Vector3((float)BitConverter.ToDouble(bytes, 0x40),
                    (float)BitConverter.ToDouble(bytes, 0x48), (float)BitConverter.ToDouble(bytes, 0x50));
                var qLen = q.LengthSquared();
                if (!float.IsFinite(qLen) || qLen < 0.25f || qLen > 4f ||
                    !float.IsFinite(t.X) || !float.IsFinite(t.Y) || !float.IsFinite(t.Z) ||
                    !float.IsFinite(s.X) || !float.IsFinite(s.Y) || !float.IsFinite(s.Z) ||
                    MathF.Abs(t.X) > 1e7f || MathF.Abs(t.Y) > 1e7f || MathF.Abs(t.Z) > 1e7f ||
                    MathF.Abs(s.X) > 100f || MathF.Abs(s.Y) > 100f || MathF.Abs(s.Z) > 100f ||
                    s.X < 0.001f || s.Y < 0.001f || s.Z < 0.001f) return false;
                transform = new ComponentTransform(Quaternion.Normalize(q), t, s, true);
                return true;
            }
            catch { return false; }
        }

        private static Vector3 TransformPoint(Vector3 local, ComponentTransform transform)
        {
            var scaled = local * transform.Scale;
            return Vector3.Transform(scaled, transform.Rotation) + transform.Translation;
        }

        private static bool TryReadTransformTranslation(byte[] bytes, out Vector3 translation)
        {
            translation = default;
            if (bytes.Length < 0x38) return false;
            try
            {
                // Wardogs uses the LWC/double FTransform layout.  Keep a
                // float fallback for transitional SDK layouts.
                var wide = new Vector3((float)BitConverter.ToDouble(bytes, 0x20),
                    (float)BitConverter.ToDouble(bytes, 0x28),
                    (float)BitConverter.ToDouble(bytes, 0x30));
                if (float.IsFinite(wide.X) && float.IsFinite(wide.Y) && float.IsFinite(wide.Z) &&
                    MathF.Abs(wide.X) < 1e7f && MathF.Abs(wide.Y) < 1e7f && MathF.Abs(wide.Z) < 1e7f)
                {
                    translation = wide;
                    return true;
                }
                if (bytes.Length >= 0x24)
                {
                    var legacy = new Vector3(BitConverter.ToSingle(bytes, 0x10),
                        BitConverter.ToSingle(bytes, 0x14), BitConverter.ToSingle(bytes, 0x18));
                    if (float.IsFinite(legacy.X) && float.IsFinite(legacy.Y) && float.IsFinite(legacy.Z) &&
                        MathF.Abs(legacy.X) < 1e7f && MathF.Abs(legacy.Y) < 1e7f && MathF.Abs(legacy.Z) < 1e7f)
                    {
                        translation = legacy;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private string GetSkeletonDiagSummary()
        {
            lock (_skeletonDiagSync)
            {
                return $"enabled={_settings.ShowSkeleton},calls={Interlocked.Read(ref _skeletonCalls)},success={Interlocked.Read(ref _skeletonSuccesses)},failures={Interlocked.Read(ref _skeletonFailures)},cacheHits={Interlocked.Read(ref _skeletonCacheHits)},last={_lastSkeletonDiag}";
            }
        }

        private void RefreshPlayerSources(ulong world)
        {
            var players = new HashSet<ulong>();
            var previousLocalPawn = _localPawn;
            Dictionary<ulong, PlayerVisualMeta> metadata;
            lock (_sourceSync) metadata = new Dictionary<ulong, PlayerVisualMeta>(_playerMeta);
            var metadataIntervalMs = Math.Clamp(_settings.SourceRefreshMs * 2, 500, 5000);
            var refreshMetadata = DateTime.UtcNow - _lastMetadataRefreshUtc >= TimeSpan.FromMilliseconds(metadataIntervalMs);
            AddPlayerStatePawns(world, players, metadata, refreshMetadata, out var playerStates, out var playerPawns);
            AddLocalControllerPawns(players, out var localPawns);
            if (IsPlausiblePointer(_lastLocalPlayerPawn))
                players.Add(_lastLocalPlayerPawn);
            // Level arrays can contain replicated pawns before they appear in
            // GameState.PlayerArray. Resolve APawn::PlayerState directly so
            // distant players do not remain "enemy/unknown" until proximity
            // causes a replication update.
            HashSet<ulong> levelCandidates;
            lock (_sourceSync) levelCandidates = new HashSet<ulong>(_levelTargetActors);
            var controllerPawn = ReadControllerPawn(_cachedController);
            var controllerInfo = controllerPawn == 0 ? default : GetClassInfoForActor(controllerPawn);
            var localPawn = controllerPawn;
            if (controllerPawn != 0)
            {
                lock (_sourceSync) _activeLocalActor = controllerPawn;
                if (controllerInfo.IsPlayer || controllerInfo.IsVehicle)
                {
                    // Use the currently possessed pawn for the radar anchor.
                    // The character pawn is still retained separately for
                    // metadata and for the instant the player exits the
                    // mortar view.
                    if (controllerInfo.IsPlayer) _lastLocalPlayerPawn = controllerPawn;
                }
                else
                {
                    // Keep the raw controller actor as the active anchor even
                    // if its class is not one of the radar target classes.
                    // The synthetic camera contact below covers this short
                    // transition without reviving the stale character point.
                    localPawn = IsPlausiblePointer(_lastLocalPlayerPawn) ? _lastLocalPlayerPawn : previousLocalPawn;
                }
            }
            else
            {
                // During mortar operation Controller.Pawn points at the
                // mortar vehicle. Preserve the last confirmed character
                // pawn first, then fall back to the longer-lived cache.
                if (IsPlausiblePointer(previousLocalPawn) && GetClassInfoForActor(previousLocalPawn).IsPlayer)
                    _lastLocalPlayerPawn = previousLocalPawn;
                localPawn = IsPlausiblePointer(_lastLocalPlayerPawn) ? _lastLocalPlayerPawn : previousLocalPawn;
                lock (_sourceSync) _activeLocalActor = localPawn;
            }
            lock (_sourceSync) _localPawn = localPawn;
            if (refreshMetadata)
            {
                var directPairs = ReadPawnPlayerStates(levelCandidates);
                ReadPlayerVisualMetadataBatch(directPairs, metadata);
                HashSet<ulong> vehicles;
                lock (_sourceSync) vehicles = new HashSet<ulong>(_globalVehicleActors);
                ReadVehicleVisualMetadataBatch(vehicles, metadata);
                if (localPawn != 0 && (!metadata.TryGetValue(localPawn, out var currentLocalMeta) ||
                    currentLocalMeta.Team < 0 || string.IsNullOrWhiteSpace(currentLocalMeta.FactionName)))
                    ReadPlayerVisualMetadataBatch(ReadPawnPlayerStates(new[] { localPawn }), metadata);
                _lastMetadataRefreshUtc = DateTime.UtcNow;
                if (localPawn != 0 && metadata.TryGetValue(localPawn, out var localMeta))
                {
                    var oldTeam = _localTeam;
                    _localTeam = localMeta.Team;
                    _localFactionKey = localMeta.FactionKey;
                    var oldFactionName = _localFactionName;
                    _localFactionName = localMeta.FactionName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(_localFactionName))
                        _localFactionName = CanonicalFactionForTeam(_localTeam);
                    if ((_localTeam >= 0 && _localTeam != oldTeam) ||
                        !string.Equals(_localFactionName, oldFactionName, StringComparison.OrdinalIgnoreCase))
                        LogMessage(2, $"ESP local team resolved: team={_localTeam}, faction={_localFactionName}, factionData=0x{_localFactionKey:X}");
                }
            }
            lock (_sourceSync)
            {
                _playerSourceActors = players;
                _playerMeta = metadata;
                _playerStateCount = playerStates;
                _playerPawnCount = playerPawns;
                _localPawnCount = localPawns;
            }
        }

        private List<(ulong State, ulong Pawn)> ReadPawnPlayerStates(IEnumerable<ulong> pawns)
        {
            var list = pawns.Where(IsPlausiblePointer).Distinct().ToArray();
            var result = new List<(ulong, ulong)>();
            if (list.Length == 0) return result;
            using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
            foreach (var pawn in list) scatter.Prepare(pawn + Offsets.PawnPlayerState, 8);
            if (!scatter.Execute()) return result;
            foreach (var pawn in list)
            {
                var data = scatter.Read(pawn + Offsets.PawnPlayerState, 8);
                if (data is null || data.Length != 8) continue;
                var state = BinaryPrimitives.ReadUInt64LittleEndian(data);
                if (IsPlausiblePointer(state)) result.Add((state, pawn));
            }
            return result;
        }

        private void RefreshLevelSources(ulong world)
        {
            var levels = ReadLoadedLevels(world);
            var levelActors = ReadLevelActors(levels);
            var identities = ReadActorIdentities(levelActors);
            var targets = new HashSet<ulong>();
            var vehicleTargets = new HashSet<ulong>();
            var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var identity in identities)
            {
                var info = GetClassInfo(identity.Class);
                classCounts[info.Name] = classCounts.GetValueOrDefault(info.Name) + 1;
                if (info.IsPlayer || info.IsVehicle)
                {
                    targets.Add(identity.Actor);
                    if (info.IsVehicle) vehicleTargets.Add(identity.Actor);
                }
            }

            var topClasses = classCounts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(pair => $"{pair.Key}={pair.Value}")
                .ToArray();
            lock (_sourceSync)
            {
                _levelTargetActors = targets;
                _levelVehicleActors = vehicleTargets;
                _levelCount = levels.Count;
                _levelActorCount = levelActors.Count;
                _sourceClassNames = topClasses;
            }
        }

        private bool LooksLikeVehicle(ulong actor)
        {
            try
            {
                var mesh = TryReadPtr(actor + Offsets.VehicleMesh);
                if (mesh == 0) return false;
                var meshClass = TryReadPtr(mesh + Offsets.UObjectClass);
                if (meshClass == 0) return false;
                return TryReadPtr(actor + Offsets.VehiclePlayerState) != 0 || meshClass != 0;
            }
            catch { return false; }
        }

        private HashSet<ulong> ReadLevelActors(IReadOnlyList<ulong> levels)
        {
            var actors = new HashSet<ulong>();
            var validLevels = levels.Where(IsPlausiblePointer).Distinct().ToArray();
            if (validLevels.Length == 0) return actors;
            var containers = new List<ulong>(validLevels.Length);
            using (var containerScatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE))
            {
                foreach (var level in validLevels)
                    containerScatter.Prepare(level + Offsets.LevelActorContainer, 8);
                if (!containerScatter.Execute()) return actors;
                foreach (var level in validLevels)
                {
                    var data = containerScatter.Read(level + Offsets.LevelActorContainer, 8);
                    if (data is byte[] bytes && bytes.Length == 8)
                    {
                        var container = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                        if (IsPlausiblePointer(container)) containers.Add(container);
                    }
                }
            }
            var arrays = new List<(ulong Address, int Count)>(containers.Count);
            using (var headerScatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE))
            {
                foreach (var container in containers)
                {
                    headerScatter.Prepare(container + Offsets.ActorArray, 8);
                    headerScatter.Prepare(container + Offsets.ActorArrayCount, 4);
                }
                if (!headerScatter.Execute()) return actors;
                foreach (var container in containers)
                {
                    var arrayBytes = headerScatter.Read(container + Offsets.ActorArray, 8);
                    var countBytes = headerScatter.Read(container + Offsets.ActorArrayCount, 4);
                    if (arrayBytes is not byte[] ab || ab.Length != 8 ||
                        countBytes is not byte[] cb || cb.Length != 4) continue;
                    var address = BinaryPrimitives.ReadUInt64LittleEndian(ab);
                    var count = BinaryPrimitives.ReadInt32LittleEndian(cb);
                    if (IsPlausiblePointer(address) && count > 0 && count <= 50000)
                        arrays.Add((address, count));
                }
            }
            using (var actorScatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE))
            {
                foreach (var array in arrays)
                    actorScatter.Prepare(array.Address, checked((uint)(array.Count * 8)));
                if (!actorScatter.Execute()) return actors;
                foreach (var array in arrays)
                {
                    var data = actorScatter.Read(array.Address, checked((uint)(array.Count * 8)));
                    if (data is null || data.Length != array.Count * 8) continue;
                    for (var i = 0; i < array.Count; i++)
                    {
                        var actor = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(i * 8, 8));
                        if (IsPlausiblePointer(actor)) actors.Add(actor);
                    }
                }
            }
            return actors;
        }

        private List<ActorIdentity> ReadActorIdentities(IEnumerable<ulong> actors)
        {
            var actorList = actors.Where(IsPlausiblePointer).Distinct().ToArray();
            var result = new List<ActorIdentity>(actorList.Length);
            const int batchSize = 4096;
            for (var start = 0; start < actorList.Length; start += batchSize)
            {
                var length = Math.Min(batchSize, actorList.Length - start);
                using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                for (var i = 0; i < length; i++)
                {
                    var actor = actorList[start + i];
                    scatter.Prepare(actor + Offsets.ActorClass, 8);
                    scatter.Prepare(actor + Offsets.ActorRootComponent, 8);
                }
                var scatterOk = scatter.Execute();
                for (var i = 0; i < length; i++)
                {
                    var actor = actorList[start + i];
                    byte[]? classData = scatterOk ? scatter.Read(actor + Offsets.ActorClass, 8) : null;
                    byte[]? rootData = scatterOk ? scatter.Read(actor + Offsets.ActorRootComponent, 8) : null;
                    if (classData is null || classData.Length != 8)
                    {
                        try { classData = ReadExact(_process, actor + Offsets.ActorClass, 8); } catch { classData = null; }
                    }
                    if (rootData is null || rootData.Length != 8)
                    {
                        try { rootData = ReadExact(_process, actor + Offsets.ActorRootComponent, 8); } catch { rootData = null; }
                    }
                    if (classData is null || classData.Length != 8 || rootData is null || rootData.Length != 8) continue;
                    var classPtr = BinaryPrimitives.ReadUInt64LittleEndian(classData);
                    var root = BinaryPrimitives.ReadUInt64LittleEndian(rootData);
                    if (IsPlausiblePointer(classPtr) && IsPlausiblePointer(root))
                        result.Add(new ActorIdentity(actor, classPtr, root));
                }
            }
            return result;
        }

        private void RefreshTargetIdentities(bool force = false)
        {
            lock (_identityRefreshSync)
            {
                if (!force && DateTime.UtcNow - _lastIdentityRefreshUtc < TimeSpan.FromSeconds(1))
                    return;
                HashSet<ulong> actors;
                lock (_sourceSync)
                {
                    actors = new HashSet<ulong>(_levelTargetActors);
                    actors.UnionWith(_playerSourceActors);
                    actors.UnionWith(_globalVehicleActors);
                    if (actors.SetEquals(_identityActors)) return;
                }

                var identities = ReadActorIdentities(actors)
                    .Where(identity =>
                    {
                        var info = GetActorClassInfo(identity.Actor, identity.Class);
                        return info.IsPlayer || info.IsVehicle;
                    })
                    .ToArray();
                lock (_sourceSync)
                {
                    _identityActors = actors;
                    _targetIdentities = identities;
                }
                _lastIdentityRefreshUtc = DateTime.UtcNow;
            }
        }

        private List<ActorFrameData> ReadActorFrameData(IReadOnlyList<ActorIdentity> identities)
        {
            var actorStarted = Stopwatch.GetTimestamp();
            var result = new List<ActorFrameData>(identities.Count);
            if (identities.Count == 0) return result;
            var readWeapons = _settings.ShowWeapons || _settings.RadarShowWeapons;
            var refreshWeapons = readWeapons && DateTime.UtcNow - _lastWeaponRefreshUtc >= TimeSpan.FromMilliseconds(1000);

            var scatterAddressCount = 0;
            var worldTransformCount = 0;
            var worldTransformOffsets = new Dictionary<ulong, int>();
            HashSet<ulong> skeletonPrefilter;
            lock (_skeletonPrefilterSync) skeletonPrefilter = new HashSet<ulong>(_previousVisibleSkeletonActors);
            // Prime the mesh cache on the first frame (or after a level
            // transition).  Waiting for the prefilter to become non-empty
            // otherwise creates a deadlock: no mesh is read, therefore no
            // skeleton can be projected, therefore the prefilter stays empty.
            var primeSkeletonMeshes = _settings.ShowSkeleton && skeletonPrefilter.Count == 0;
            var scatterStart = Stopwatch.GetTimestamp();
            using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
            foreach (var identity in identities)
            {
                // UE5 FVector is double precision in this build.
                scatter.Prepare(identity.Root + Offsets.SceneRelativeLocation, 24);
                // RelativeLocation is local when the pawn is attached to a
                // vehicle/seat. ComponentToWorld supplies the true world
                // position and keeps boxes locked to moving vehicles. The
                // generated SDK omits this field, so stage all known slots.
                foreach (var ctwOffset in Offsets.ComponentToWorldCandidates)
                    scatter.Prepare(identity.Root + ctwOffset, 0x60);
                // The supplied LWC SDK declares FRotator as 0x18 (three
                // doubles). Keep a 24-byte read and fall back to the legacy
                // 3-float layout below for older builds.
                scatter.Prepare(identity.Root + Offsets.SceneRelativeRotation, 24);
                if (GetActorClassInfo(identity.Actor, identity.Class).IsPlayer)
                {
                    if (_settings.ShowSkeleton && (primeSkeletonMeshes || skeletonPrefilter.Contains(identity.Actor)))
                        scatter.Prepare(identity.Actor + Offsets.MoverMesh, 8);
                    if (refreshWeapons) scatter.Prepare(identity.Actor + Offsets.PlayerInventory, 8);
                }
            }
            scatterAddressCount = identities.Count * (2 + Offsets.ComponentToWorldCandidates.Length);
            if (_settings.ShowSkeleton)
                scatterAddressCount += primeSkeletonMeshes
                    ? identities.Count(x => GetActorClassInfo(x.Actor, x.Class).IsPlayer)
                    : skeletonPrefilter.Count;
            if (!scatter.Execute()) return result;
            var inventories = new Dictionary<ulong, ulong>();
            foreach (var identity in identities)
            {
                var data = scatter.Read(identity.Root + Offsets.SceneRelativeLocation, 24);
                if (data is null || data.Length != 24) continue;
                var rotationData = scatter.Read(identity.Root + Offsets.SceneRelativeRotation, 24);
                var relativeLocation = new Vector3(
                    (float)BitConverter.ToDouble(data, 0),
                    (float)BitConverter.ToDouble(data, 8),
                    (float)BitConverter.ToDouble(data, 16));
                var location = relativeLocation;
                ComponentTransform worldTransform = default;
                var worldTransformValid = false;
                if (_componentTransformOffsetCache.TryGetValue(identity.Root, out var cachedCtw))
                {
                    var cachedBytes = scatter.Read(identity.Root + cachedCtw, 0x60);
                    if (cachedBytes is byte[] cb && TryParseComponentTransform(cb, out var parsedCached))
                    {
                        worldTransform = parsedCached;
                        worldTransformValid = true;
                        worldTransformOffsets[cachedCtw] = worldTransformOffsets.TryGetValue(cachedCtw, out var cc) ? cc + 1 : 1;
                    }
                    else
                    {
                        _componentTransformOffsetCache.TryRemove(identity.Root, out _);
                    }
                }
                if (!worldTransformValid)
                {
                    foreach (var ctwOffset in Offsets.ComponentToWorldCandidates)
                    {
                        var worldBytes = scatter.Read(identity.Root + ctwOffset, 0x60);
                        if (worldBytes is not byte[] wb || !TryParseComponentTransform(wb, out var parsed)) continue;
                        worldTransform = parsed;
                        worldTransformValid = true;
                        _componentTransformOffsetCache[identity.Root] = ctwOffset;
                        worldTransformOffsets[ctwOffset] = worldTransformOffsets.TryGetValue(ctwOffset, out var nc) ? nc + 1 : 1;
                        break;
                    }
                }
                if (worldTransformValid)
                {
                    location = worldTransform.Translation;
                    worldTransformCount++;
                }
                var yaw = 0f;
                if (rotationData is byte[] rd && rd.Length == 24)
                {
                    var wideYaw = (float)BitConverter.ToDouble(rd, 8);
                    if (float.IsFinite(wideYaw) && MathF.Abs(wideYaw) <= 360f)
                        yaw = wideYaw;
                    else
                    {
                        var legacyYaw = BitConverter.ToSingle(rd, 4);
                        if (float.IsFinite(legacyYaw) && MathF.Abs(legacyYaw) <= 360f) yaw = legacyYaw;
                    }
                }
                if (!float.IsFinite(yaw) || MathF.Abs(yaw) > 360f) yaw = 0;
                var mesh = 0UL;
                if (GetActorClassInfo(identity.Actor, identity.Class).IsPlayer)
                {
                    if (_settings.ShowSkeleton && (primeSkeletonMeshes || skeletonPrefilter.Contains(identity.Actor)))
                    {
                        var meshData = scatter.Read(identity.Actor + Offsets.MoverMesh, 8);
                        if (meshData is byte[] mb && mb.Length == 8) mesh = BinaryPrimitives.ReadUInt64LittleEndian(mb);
                    }
                    if (refreshWeapons)
                    {
                        var inventoryData = scatter.Read(identity.Actor + Offsets.PlayerInventory, 8);
                        if (inventoryData is byte[] ib && ib.Length == 8)
                        {
                            var inventory = BinaryPrimitives.ReadUInt64LittleEndian(ib);
                            if (IsPlausiblePointer(inventory)) inventories[identity.Actor] = inventory;
                        }
                    }
                }
                if (float.IsFinite(location.X) && float.IsFinite(location.Y) && float.IsFinite(location.Z))
                    result.Add(new ActorFrameData(identity.Actor, identity.Class, location, mesh, yaw, string.Empty));
            }
            if (refreshWeapons && inventories.Count > 0)
            {
                var heldItems = new Dictionary<ulong, ulong>();
                using var itemScatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                foreach (var inventory in inventories.Values.Distinct())
                {
                    itemScatter.Prepare(inventory + Offsets.InventoryHeldItem, 8);
                    itemScatter.Prepare(inventory + Offsets.InventoryActiveItem, 8);
                }
                if (itemScatter.Execute())
                {
                    foreach (var pair in inventories)
                    {
                        var data = itemScatter.Read(pair.Value + Offsets.InventoryHeldItem, 8);
                        ulong item = data is byte[] bytes && bytes.Length == 8
                            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
                        if (!IsPlausiblePointer(item))
                        {
                            data = itemScatter.Read(pair.Value + Offsets.InventoryActiveItem, 8);
                            item = data is byte[] bytes2 && bytes2.Length == 8
                                ? BinaryPrimitives.ReadUInt64LittleEndian(bytes2) : 0;
                        }
                        if (IsPlausiblePointer(item))
                        {
                            heldItems[pair.Key] = item;
                        }
                    }
                }
                if (heldItems.Count > 0)
                {
                    var weaponNames = new Dictionary<ulong, string>();
                    foreach (var pair in heldItems)
                    {
                        try
                        {
                            var itemClass = TryReadPtr(pair.Value + Offsets.UObjectClass);
                            if (itemClass == 0) continue;
                            if (_weaponNameCache.TryGetValue(pair.Value, out var cachedName))
                            {
                                weaponNames[pair.Key] = cachedName;
                                continue;
                            }
                            var name = _names.Resolve(ReadUInt32(itemClass + Offsets.ClassNameId));
                            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("Name_", StringComparison.Ordinal))
                            {
                                var clean = CleanWeaponName(name);
                                _weaponNameCache[pair.Value] = clean;
                                weaponNames[pair.Key] = clean;
                            }
                        }
                        catch { }
                    }
                    if (weaponNames.Count > 0)
                    {
                        for (var i = 0; i < result.Count; i++)
                        {
                            var row = result[i];
                            if (weaponNames.TryGetValue(row.Actor, out var weapon))
                            {
                                _actorWeaponCache[row.Actor] = weapon;
                                result[i] = row with { Weapon = weapon };
                            }
                        }
                    }
                }
                _lastWeaponRefreshUtc = DateTime.UtcNow;
            }
            if (readWeapons && _actorWeaponCache.Count > 0)
                for (var i = 0; i < result.Count; i++)
                    if (_actorWeaponCache.TryGetValue(result[i].Actor, out var cachedWeapon))
                        result[i] = result[i] with { Weapon = cachedWeapon };
            _lastScatterAddressCount = scatterAddressCount;
            _lastScatterBonePlayers = result.Count(x => x.Mesh != 0);
            _lastScatterMs = Stopwatch.GetElapsedTime(scatterStart).TotalMilliseconds;
            _lastActorFrameMs = Stopwatch.GetElapsedTime(actorStarted).TotalMilliseconds;
            _lastWorldTransformCount = worldTransformCount;
            _lastWorldTransformOffset = worldTransformOffsets.Count == 0
                ? 0
                : worldTransformOffsets.OrderByDescending(x => x.Value).First().Key;
            if (DateTime.UtcNow - _lastProjectionDiagUtc > TimeSpan.FromSeconds(2))
                LogMessage(2, $"ESP SG: addresses={_lastScatterAddressCount}, bonePlayers={_lastScatterBonePlayers}, dmaMs={_lastScatterMs:0.00}");
            return result;
        }

        private static string CleanWeaponName(string value)
        {
            var name = value;
            var slash = name.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < name.Length) name = name[(slash + 1)..];
            foreach (var suffix in new[] { "_C", "_BP" })
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
            return name.Replace('_', ' ');
        }

        private ActorClassInfo GetClassInfo(ulong classPtr) => _classInfo.GetOrAdd(classPtr, candidate =>
        {
            if (!IsPlausiblePointer(candidate)) return default;
            var directName = _names.Resolve(ReadUInt32(candidate + Offsets.ClassNameId));
            var isPlayer = false;
            var isVehicle = false;
            var hierarchy = candidate;
            for (var depth = 0; IsPlausiblePointer(hierarchy) && depth < 16; depth++)
            {
                var name = hierarchy == candidate
                    ? directName
                    : _names.Resolve(ReadUInt32(hierarchy + Offsets.ClassNameId));
                isPlayer |= hierarchy == _playerBaseClass ||
                            name.Contains("WDMoverCharacter", StringComparison.OrdinalIgnoreCase);
                isVehicle |= hierarchy == _vehicleBaseClass || hierarchy == _modularVehicleClass ||
                             name.Contains("BHBaseVehiclePawn", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("ModularVehicle", StringComparison.OrdinalIgnoreCase);
                if (isPlayer || isVehicle) break;
                hierarchy = TryReadPtr(hierarchy + Offsets.ClassSuper);
            }
            if (directName.StartsWith("Name_", StringComparison.Ordinal))
                directName = isPlayer ? "Player" : isVehicle ? "Vehicle" : $"class@0x{candidate:X}";
            return new ActorClassInfo(directName, isPlayer, isVehicle);
        });

        private ActorClassInfo GetActorClassInfo(ulong actor, ulong classPtr)
        {
            var info = GetClassInfo(classPtr);
            if (info.IsPlayer || info.IsVehicle) return info;
            lock (_sourceSync)
            {
                if (_playerSourceActors.Contains(actor))
                    return new ActorClassInfo("Player", true, false);
                if (_globalVehicleActors.Contains(actor) || _levelVehicleActors.Contains(actor))
                    return new ActorClassInfo("Vehicle", false, true);
            }
            return info;
        }

        private IReadOnlyList<ulong> ReadLoadedLevels(ulong world)
        {
            var levels = new HashSet<ulong>();
            try
            {
                var persistentLevel = ReadPtr(world + Offsets.WorldPersistentLevel);
                if (IsPlausiblePointer(persistentLevel)) levels.Add(persistentLevel);
            }
            catch { }

            // UWorld.Levels is at +0x1C8. The two earlier ULevel arrays are
            // retained as supplemental sources for transitional streaming state.
            AddLevelArray(world + Offsets.WorldLevels, levels);
            AddLevelArray(world + Offsets.WorldLevelsAlt1, levels);
            AddLevelArray(world + Offsets.WorldLevelsAlt2, levels);
            return levels.ToArray();
        }

        private void AddLevelArray(ulong arrayAddress, HashSet<ulong> levels)
        {
            try
            {
                var levelsArray = ReadPtr(arrayAddress);
                var levelCount = ReadInt32(arrayAddress + 8);
                if (!IsPlausiblePointer(levelsArray) || levelCount <= 0 || levelCount > 4096) return;
                for (var i = 0; i < levelCount; i++)
                {
                    try
                    {
                        var level = ReadPtr(levelsArray + (ulong)i * 8);
                        if (IsPlausiblePointer(level)) levels.Add(level);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void AddPlayerStatePawns(ulong world, HashSet<ulong> actors,
            Dictionary<ulong, PlayerVisualMeta> metadata,
            bool readMetadata,
            out int playerStateCount, out int playerPawnCount)
        {
            playerStateCount = 0;
            playerPawnCount = 0;
            try
            {
                var gameState = ReadPtr(world + Offsets.WorldGameState);
                if (gameState == 0) return;
                var playerArray = ReadPtr(gameState + Offsets.GameStatePlayerArray);
                var count = ReadInt32(gameState + Offsets.GameStatePlayerArrayCount);
                if (playerArray == 0 || count <= 0 || count > 2048) return;
                var playerStateBytes = ReadExact(_process, playerArray, checked((uint)(count * 8)));
                var playerStates = new List<ulong>(count);
                for (var i = 0; i < count; i++)
                {
                    var playerState = BinaryPrimitives.ReadUInt64LittleEndian(playerStateBytes.AsSpan(i * 8, 8));
                    if (playerState != 0) playerStates.Add(playerState);
                }
                playerStateCount = playerStates.Count;
                using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                foreach (var playerState in playerStates) scatter.Prepare(playerState + Offsets.PlayerStatePawn, 8);
                if (!scatter.Execute()) return;
                var pairs = new List<(ulong State, ulong Pawn)>(playerStates.Count);
                foreach (var playerState in playerStates)
                {
                    var data = scatter.Read(playerState + Offsets.PlayerStatePawn, 8);
                    if (data is null || data.Length != 8) continue;
                    var pawn = BinaryPrimitives.ReadUInt64LittleEndian(data);
                    if (!IsPlausiblePointer(pawn)) continue;
                    playerPawnCount++;
                    actors.Add(pawn);
                    pairs.Add((playerState, pawn));
                }
                if (readMetadata) ReadPlayerVisualMetadataBatch(pairs, metadata);
            }
            catch { }
        }

        private void ReadPlayerVisualMetadataBatch(IReadOnlyList<(ulong State, ulong Pawn)> pairs,
            Dictionary<ulong, PlayerVisualMeta> metadata)
        {
            try
            {
                pairs = pairs.Where(pair => IsPlausiblePointer(pair.State) && IsPlausiblePointer(pair.Pawn))
                    .DistinctBy(pair => pair.Pawn).ToArray();
                if (pairs.Count == 0) return;
                var first = new Dictionary<ulong, (ulong FactionComponent, ulong Vitality, byte Bleedout)>();
                var nameHeaders = new Dictionary<ulong, (FStringHeader Primary, FStringHeader Sanitized)>();
                using (var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE))
                {
                    foreach (var pair in pairs)
                    {
                        scatter.Prepare(pair.State + Offsets.PlayerStateFactionComponent, 8);
                        scatter.Prepare(pair.Pawn + Offsets.VitalityComponent, 8);
                        scatter.Prepare(pair.Pawn + Offsets.BleedoutState, 1);
                        scatter.Prepare(pair.State + Offsets.PlayerName, 0x10);
                        scatter.Prepare(pair.State + Offsets.SanitizedName, 0x10);
                    }
                    if (!scatter.Execute()) return;
                    foreach (var pair in pairs)
                    {
                        var fc = ReadPtrFromScatter(scatter, pair.State + Offsets.PlayerStateFactionComponent);
                        var vitality = ReadPtrFromScatter(scatter, pair.Pawn + Offsets.VitalityComponent);
                        if (!IsPlausiblePointer(fc)) fc = 0;
                        if (!IsPlausiblePointer(vitality)) vitality = 0;
                        var bleedout = ReadByteFromScatter(scatter, pair.Pawn + Offsets.BleedoutState);
                        first[pair.Pawn] = (fc, vitality, bleedout);
                        nameHeaders[pair.State] = (
                            ReadFStringHeaderFromScatter(scatter, pair.State + Offsets.PlayerName),
                            ReadFStringHeaderFromScatter(scatter, pair.State + Offsets.SanitizedName));
                    }
                }

                var nameValues = ReadFStringValues(nameHeaders.Values
                    .SelectMany(x => new[] { x.Primary, x.Sanitized })
                    .Where(x => x.Pointer != 0)
                    .DistinctBy(x => x.Pointer)
                    .ToArray());

                var factionComponents = first.Values.Select(x => x.FactionComponent)
                    .Where(x => x != 0).Distinct().ToArray();
                var vitalityComponents = first.Values.Select(x => x.Vitality)
                    .Where(x => x != 0).Distinct().ToArray();
                var factionMap = ScatterPointers(factionComponents, Offsets.FactionComponentFaction);
                var componentTags = ScatterQwords(factionComponents, Offsets.FactionComponentTag);
                var componentTagsAlt = ScatterQwords(factionComponents, Offsets.FactionComponentTagAlt);
                var healthMap = ScatterFloats(vitalityComponents, Offsets.VitalityCurrent, Offsets.VitalityMax);
                var factionObjects = ScatterPointers(factionMap.Values, Offsets.FactionData);
                var teamData = ScatterBytes(factionObjects.Values, Offsets.GenericTeamId);
                var factionTags = ScatterQwords(factionObjects.Values, Offsets.FactionTag);
                foreach (var pair in pairs)
                {
                    var row = first[pair.Pawn];
                    var faction = factionMap.GetValueOrDefault(row.FactionComponent);
                    var factionData = factionObjects.GetValueOrDefault(faction);
                    var team = teamData.GetValueOrDefault(factionData, (byte)255);
                    var factionName = ResolveFactionName(factionData, factionTags);
                    if (string.IsNullOrWhiteSpace(factionName) && faction != 0)
                        factionName = ResolveFactionObjectName(faction);
                    if (string.IsNullOrWhiteSpace(factionName))
                    {
                        factionName = ResolveGameplayTagName(componentTags.GetValueOrDefault(row.FactionComponent));
                        if (string.IsNullOrWhiteSpace(factionName))
                            factionName = ResolveGameplayTagName(componentTagsAlt.GetValueOrDefault(row.FactionComponent));
                    }
                    // 0xFF is unassigned. Values above the small team range
                    // are treated as unknown; the readable faction tag then
                    // supplies the relation without trusting hash-like bytes.
                    if (team > 32) team = 255;
                    if (team != 255 && string.IsNullOrWhiteSpace(factionName))
                        factionName = CanonicalFactionForTeam(team);
                    var health = default((float Current, float Max));
                    var hasHealth = row.Vitality != 0 && healthMap.TryGetValue(row.Vitality, out health);
                    // EWDBleedoutState: BleedingOut=0, HoldingOn=1, GivingUp=2.
                    // BleedingOut alone is not enough to mark a pawn downed; the
                    // replicated standing-health value must also be exhausted.
                    var downed = row.Bleedout == 1 || row.Bleedout == 2 ||
                                 (row.Bleedout == 0 && hasHealth && health.Current <= 0);
                    var percent = hasHealth && health.Max > 0 ? Math.Clamp(health.Current / health.Max * 100, 0, 100) : -1;
                    var factionKey = factionData;
                    var name = ResolvePlayerName(pair.State, nameHeaders, nameValues);
                    // Keep the last authoritative relation during a transient
                    // scatter miss. This prevents a distant teammate from
                    // flickering to ENEMY/UNKNOWN until the next replication
                    // update, while still never promoting an unverified tag.
                    var hasPrior = metadata.TryGetValue(pair.Pawn, out var prior);
                    var resolvedTeam = team == 255
                        ? (TeamForFaction(factionName) ?? (string.IsNullOrWhiteSpace(factionName) && hasPrior ? prior.Team : -1))
                        : team;
                    var resolvedFaction = factionKey != 0 ? factionKey : hasPrior ? prior.FactionKey : 0;
                    var resolvedFactionName = !string.IsNullOrWhiteSpace(factionName) ? factionName : hasPrior ? prior.FactionName : string.Empty;
                    metadata[pair.Pawn] = new PlayerVisualMeta(resolvedTeam,
                        resolvedFaction, resolvedFactionName, downed, percent, name);
                }
            }
            catch { }
        }

        private void ReadVehicleVisualMetadataBatch(IEnumerable<ulong> vehicles,
            Dictionary<ulong, PlayerVisualMeta> metadata)
        {
            var list = vehicles.Where(IsPlausiblePointer).Distinct().ToArray();
            if (list.Length == 0) return;
            try
            {
                using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                foreach (var vehicle in list)
                {
                    foreach (var offset in GetVehicleFactionOffsets(vehicle))
                        scatter.Prepare(vehicle + offset, 8);
                    // BHBaseVehiclePawn keeps the owning/occupying player
                    // state at +0x848. APawn::PlayerState (+0x2D8) is used as
                    // a fallback for vehicle subclasses that do not populate
                    // the dedicated owner field.
                    scatter.Prepare(vehicle + Offsets.VehiclePlayerState, 8);
                    scatter.Prepare(vehicle + Offsets.PawnPlayerState, 8);
                }
                if (!scatter.Execute()) return;
                var directCandidates = new Dictionary<ulong, List<ulong>>(list.Length);
                foreach (var vehicle in list)
                {
                    var candidates = new List<ulong>();
                    foreach (var offset in GetVehicleFactionOffsets(vehicle))
                    {
                        var component = ReadPtrFromScatter(scatter, vehicle + offset);
                        if (IsPlausiblePointer(component) && !candidates.Contains(component))
                            candidates.Add(component);
                    }
                    if (candidates.Count > 0) directCandidates[vehicle] = candidates;
                }
                // The vehicle's own faction is authoritative even when it is
                // empty or occupied by a player from another replicated pawn.
                var directFactionVehicles = ReadVehicleDirectFactionMetadataBatch(directCandidates, metadata);

                var pairs = new List<(ulong State, ulong Pawn)>(list.Length);
                foreach (var vehicle in list)
                {
                    if (directFactionVehicles.Contains(vehicle)) continue;
                    var state = ReadPtrFromScatter(scatter, vehicle + Offsets.VehiclePlayerState);
                    if (!IsPlausiblePointer(state))
                        state = ReadPtrFromScatter(scatter, vehicle + Offsets.PawnPlayerState);
                    if (IsPlausiblePointer(state)) pairs.Add((state, vehicle));
                }
                // Reuse the authoritative faction/GenericTeamId path. The
                // vehicle-only entries are rendered as vehicles, so their
                // health/downed/name fields are not shown in the overlay.
                ReadPlayerVisualMetadataBatch(pairs, metadata);
            }
            catch { }
        }

        private IReadOnlyList<ulong> GetVehicleFactionOffsets(ulong vehicle)
        {
            // The SDK gives each vehicle family its own component slot. Put
            // that slot first so an unused inherited pointer cannot win the
            // direct-faction probe; retain all confirmed slots as fallback.
            var name = string.Empty;
            try { name = GetClassInfoForActor(vehicle).Name; } catch { }
            ulong preferred = 0;
            if (name.Contains("Wheeled", StringComparison.OrdinalIgnoreCase))
                preferred = Offsets.WheeledVehicleFactionComponent;
            else if (name.Contains("Tracked", StringComparison.OrdinalIgnoreCase))
                preferred = Offsets.TrackedVehicleFactionComponent;
            else if (name.Contains("Rotary", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Helicopter", StringComparison.OrdinalIgnoreCase))
                preferred = Offsets.RotaryVehicleFactionComponent;
            else if (name.Contains("Airplane", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Aircraft", StringComparison.OrdinalIgnoreCase))
                preferred = Offsets.AirplaneVehicleFactionComponent;
            else if (name.Contains("Stationary", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Emplacement", StringComparison.OrdinalIgnoreCase))
                preferred = Offsets.StationaryVehicleFactionComponent;

            if (preferred == 0) return Offsets.VehicleFactionComponentCandidates;
            return new[] { preferred }
                .Concat(Offsets.VehicleFactionComponentCandidates.Where(x => x != preferred))
                .ToArray();
        }

        private HashSet<ulong> ReadVehicleDirectFactionMetadataBatch(
            IReadOnlyDictionary<ulong, List<ulong>> actorCandidates,
            Dictionary<ulong, PlayerVisualMeta> metadata)
        {
            var resolvedActors = new HashSet<ulong>();
            if (actorCandidates.Count == 0) return resolvedActors;
            try
            {
                var allComponents = actorCandidates.Values.SelectMany(x => x).Distinct().ToArray();
                var factionMap = ScatterPointers(allComponents, Offsets.FactionComponentFaction);
                var componentTags = ScatterQwords(allComponents, Offsets.FactionComponentTag);
                var componentTagsAlt = ScatterQwords(allComponents, Offsets.FactionComponentTagAlt);
                var factionObjects = ScatterPointers(factionMap.Values, Offsets.FactionData);
                var teamData = ScatterBytes(factionObjects.Values, Offsets.GenericTeamId);
                var factionTags = ScatterQwords(factionObjects.Values, Offsets.FactionTag);
                foreach (var pair in actorCandidates)
                {
                    var team = -1;
                    var factionData = 0UL;
                    var factionName = string.Empty;
                    foreach (var component in pair.Value)
                    {
                        var faction = factionMap.GetValueOrDefault(component);
                        var candidateData = factionObjects.GetValueOrDefault(faction);
                        var candidateTeamByte = teamData.GetValueOrDefault(candidateData, (byte)255);
                        var candidateName = ResolveFactionName(candidateData, factionTags);
                        if (string.IsNullOrWhiteSpace(candidateName) && faction != 0)
                            candidateName = ResolveFactionObjectName(faction);
                        if (string.IsNullOrWhiteSpace(candidateName))
                        {
                            candidateName = ResolveGameplayTagName(componentTags.GetValueOrDefault(component));
                            if (string.IsNullOrWhiteSpace(candidateName))
                                candidateName = ResolveGameplayTagName(componentTagsAlt.GetValueOrDefault(component));
                        }
                        if (candidateTeamByte != 255 && candidateTeamByte <= 32 && string.IsNullOrWhiteSpace(candidateName))
                            candidateName = CanonicalFactionForTeam(candidateTeamByte);
                        // Require a complete component -> faction -> data
                        // chain. This rejects unrelated pointers found in a
                        // different vehicle subclass' unused slot.
                        if ((candidateData == 0 && string.IsNullOrWhiteSpace(candidateName)) ||
                            (candidateTeamByte == 255 && string.IsNullOrWhiteSpace(candidateName)) ||
                            (candidateTeamByte > 32 && string.IsNullOrWhiteSpace(candidateName)))
                            continue;
                        factionData = candidateData;
                        team = candidateTeamByte == 255 || candidateTeamByte > 32 ? -1 : candidateTeamByte;
                        factionName = candidateName;
                        break;
                    }
                    if (team < 0 && string.IsNullOrWhiteSpace(factionName)) continue;
                    var hasPrior = metadata.TryGetValue(pair.Key, out var prior);
                    metadata[pair.Key] = new PlayerVisualMeta(
                        team >= 0 ? team : TeamForFaction(factionName) ?? (string.IsNullOrWhiteSpace(factionName) && hasPrior ? prior.Team : -1),
                        factionData != 0 ? factionData : hasPrior ? prior.FactionKey : 0,
                        !string.IsNullOrWhiteSpace(factionName) ? factionName : hasPrior ? prior.FactionName : string.Empty,
                        hasPrior && prior.Downed, hasPrior ? prior.Health : -1, hasPrior ? prior.Name : string.Empty);
                    resolvedActors.Add(pair.Key);
                }
            }
            catch { }
            return resolvedActors;
        }

        private string ResolveFactionName(ulong factionData,
            IReadOnlyDictionary<ulong, ulong> factionTags)
        {
            if (factionData != 0 && factionTags.TryGetValue(factionData, out var tagWord))
            {
                var fromTag = ResolveGameplayTagName(tagWord);
                if (!string.IsNullOrWhiteSpace(fromTag)) return fromTag;
            }
            // FactionData is a UObject. Its object name is stable even when
            // the replicated FGameplayTag has not arrived yet.
            if (factionData != 0)
            {
                try
                {
                    var objectName = _names.Resolve(ReadUInt32(factionData + Offsets.UObjectNameId));
                    var fromObject = CanonicalFactionFromText(objectName);
                    if (!string.IsNullOrWhiteSpace(fromObject)) return fromObject;
                }
                catch { }
            }
            return string.Empty;
        }

        private string ResolveFactionObjectName(ulong factionObject)
        {
            if (factionObject == 0) return string.Empty;
            try
            {
                var objectName = _names.Resolve(ReadUInt32(factionObject + Offsets.UObjectNameId));
                return CanonicalFactionFromText(objectName);
            }
            catch { return string.Empty; }
        }

        private static string CanonicalFactionForTeam(int team) => team switch
        {
            0 => "LONESTAR",
            1 => "VALKYRA",
            2 => "MANTICORE",
            _ => string.Empty
        };

        private static int? TeamForFaction(string? faction) => faction?.ToUpperInvariant() switch
        {
            "LONESTAR" => 0,
            "VALKYRA" => 1,
            "MANTICORE" => 2,
            _ => null
        };

        private static string CanonicalFactionFromText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var upper = value.ToUpperInvariant();
            foreach (var known in new[] { "LONESTAR", "VALKYRA", "MANTICORE" })
                if (upper.Contains(known, StringComparison.Ordinal)) return known;
            return string.Empty;
        }

        private string ResolveGameplayTagName(ulong tagWord)
        {
            var tagId = unchecked((uint)(tagWord & 0xFFFFFFFF));
            if (tagId == 0) return string.Empty;
            var raw = _names.Resolve(tagId);
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("Name_", StringComparison.Ordinal))
                return string.Empty;
            var fromText = CanonicalFactionFromText(raw);
            if (!string.IsNullOrWhiteSpace(fromText)) return fromText;
            // Do not turn an unrelated tag (or a transient "None") into a
            // fourth faction: GenericTeamId remains the fallback relation.
            return string.Empty;
        }

        private string ResolvePlayerName(ulong state,
            IReadOnlyDictionary<ulong, (FStringHeader Primary, FStringHeader Sanitized)> headers,
            IReadOnlyDictionary<ulong, string> values)
        {
            if (headers.TryGetValue(state, out var pair))
            {
                if (pair.Primary.Pointer != 0 && values.TryGetValue(pair.Primary.Pointer, out var primary) &&
                    !string.IsNullOrWhiteSpace(primary))
                {
                    _playerNameCache[state] = primary;
                    return primary;
                }
                if (pair.Sanitized.Pointer != 0 && values.TryGetValue(pair.Sanitized.Pointer, out var sanitized) &&
                    !string.IsNullOrWhiteSpace(sanitized))
                {
                    _playerNameCache[state] = sanitized;
                    return sanitized;
                }
            }
            return _playerNameCache.GetValueOrDefault(state, string.Empty);
        }

        private Dictionary<ulong, string> ReadFStringValues(IReadOnlyList<FStringHeader> headers)
        {
            var result = new Dictionary<ulong, string>();
            var valid = headers.Where(x => IsPlausiblePointer(x.Pointer) && x.Length > 0 && x.Length <= 64)
                .GroupBy(x => x.Pointer).Select(x => x.First()).ToArray();
            if (valid.Length == 0) return result;
            try
            {
                using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                foreach (var header in valid)
                    scatter.Prepare(header.Pointer, checked((uint)(header.Length * 2)));
                if (!scatter.Execute()) return result;
                foreach (var header in valid)
                {
                    var data = scatter.Read(header.Pointer, checked((uint)(header.Length * 2)));
                    if (data is null || data.Length != header.Length * 2) continue;
                    var text = Encoding.Unicode.GetString(data).TrimEnd('\0');
                    if (!string.IsNullOrWhiteSpace(text)) result[header.Pointer] = text;
                }
            }
            catch { }
            return result;
        }

        private Dictionary<ulong, ulong> ScatterPointers(IEnumerable<ulong> bases, ulong offset)
        {
            var list = bases.Where(IsPlausiblePointer).Distinct().ToArray();
            var result = new Dictionary<ulong, ulong>(list.Length);
            if (list.Length == 0) return result;
            using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
            foreach (var address in list) scatter.Prepare(address + offset, 8);
            if (!scatter.Execute()) return result;
            foreach (var address in list)
            {
                var data = scatter.Read(address + offset, 8);
                if (data is not null && data.Length == 8)
                    result[address] = BinaryPrimitives.ReadUInt64LittleEndian(data);
            }
            return result;
        }

        private Dictionary<ulong, (float Current, float Max)> ScatterFloats(IEnumerable<ulong> bases, ulong currentOffset, ulong maxOffset)
        {
            var list = bases.Where(IsPlausiblePointer).Distinct().ToArray();
            var result = new Dictionary<ulong, (float, float)>(list.Length);
            if (list.Length == 0) return result;
            using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
            foreach (var address in list) scatter.Prepare(address + currentOffset, 8);
            if (!scatter.Execute()) return result;
            foreach (var address in list)
            {
                var data = scatter.Read(address + currentOffset, 8);
                if (data is not null && data.Length == 8)
                    result[address] = (BitConverter.ToSingle(data, 0), BitConverter.ToSingle(data, 4));
            }
            return result;
        }

        private Dictionary<ulong, byte> ScatterBytes(IEnumerable<ulong> bases, ulong offset)
        {
            var list = bases.Where(IsPlausiblePointer).Distinct().ToArray();
            var result = new Dictionary<ulong, byte>(list.Length);
            if (list.Length == 0) return result;
            using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
            foreach (var address in list) scatter.Prepare(address + offset, 1);
            if (!scatter.Execute()) return result;
            foreach (var address in list)
            {
                var data = scatter.Read(address + offset, 1);
                if (data is not null && data.Length == 1) result[address] = data[0];
            }
            return result;
        }

        private Dictionary<ulong, ulong> ScatterQwords(IEnumerable<ulong> bases, ulong offset)
        {
            var list = bases.Where(IsPlausiblePointer).Distinct().ToArray();
            var result = new Dictionary<ulong, ulong>(list.Length);
            if (list.Length == 0) return result;
            using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
            foreach (var address in list) scatter.Prepare(address + offset, 8);
            if (!scatter.Execute()) return result;
            foreach (var address in list)
            {
                var data = scatter.Read(address + offset, 8);
                if (data is not null && data.Length == 8)
                    result[address] = BinaryPrimitives.ReadUInt64LittleEndian(data);
            }
            return result;
        }

        private static ulong ReadPtrFromScatter(dynamic scatter, ulong address)
        {
            var data = scatter.Read(address, 8);
            return data is byte[] bytes && bytes.Length == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
        }

        private static byte ReadByteFromScatter(dynamic scatter, ulong address)
        {
            var data = scatter.Read(address, 1);
            return data is byte[] bytes && bytes.Length == 1 ? bytes[0] : (byte)0;
        }

        private static FStringHeader ReadFStringHeaderFromScatter(dynamic scatter, ulong address)
        {
            var data = scatter.Read(address, 0x10);
            if (data is not byte[] bytes || bytes.Length != 0x10) return default;
            var pointer = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8));
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
            if (!IsPlausiblePointer(pointer) || length <= 0 || length > 64)
                return default;
            return new FStringHeader(pointer, length);
        }

        private static bool IsPlausiblePointer(ulong value) =>
            value >= 0x10000 && value <= 0x00007FFFFFFFFFFF;

        private ulong TryReadPtr(ulong address)
        {
            try
            {
                var value = ReadPtr(address);
                return IsPlausiblePointer(value) ? value : 0;
            }
            catch { return 0; }
        }

        private void AddLocalControllerPawns(HashSet<ulong> actors, out int localPawnCount)
        {
            localPawnCount = 0;
            try
            {
                var controller = ReadLocalController();
                _cachedController = controller;
                if (controller == 0) return;
                foreach (var offset in new ulong[] { Offsets.ControllerPawnCurrent, Offsets.ControllerPawn, Offsets.ControllerPawnAlt })
                {
                    try
                    {
                        var pawn = ReadPtr(controller + offset);
                        if (pawn != 0 && actors.Add(pawn)) localPawnCount++;
                        if (pawn != 0 && GetClassInfoForActor(pawn).IsPlayer)
                            _lastLocalPlayerPawn = pawn;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private ulong ReadControllerPawn(ulong controller)
        {
            if (!IsPlausiblePointer(controller)) return 0;
            ulong playerPawn = 0;
            ulong vehiclePawn = 0;
            foreach (var offset in new ulong[] { Offsets.ControllerPawnCurrent, Offsets.ControllerPawn, Offsets.ControllerPawnAlt })
            {
                var pawn = TryReadPtr(controller + offset);
                if (pawn == 0) continue;
                var info = GetClassInfoForActor(pawn);
                if (info.IsVehicle && vehiclePawn == 0) vehiclePawn = pawn;
                else if (info.IsPlayer && playerPawn == 0) playerPawn = pawn;
            }
            // A mortar view commonly leaves the character in one controller
            // field and publishes the possessed emplacement in another. A
            // vehicle candidate therefore takes precedence; otherwise use
            // the normal character pawn.
            return vehiclePawn != 0 ? vehiclePawn : playerPawn;
        }

        private void RefreshGlobalVehicleActors()
        {
            var headers = ReadGlobalObjectHeaders();
            var classClass = ReadPtr(_vehicleBaseClass + Offsets.UObjectClass);
            var classes = headers
                .Where(header => header.Class == classClass)
                .Select(header => header.Object)
                .ToArray();
            var superByClass = ReadSuperClasses(classes);
            var vehicleClasses = classes
                .Where(candidate => IsDerivedFrom(candidate, _vehicleBaseClass, superByClass) ||
                                    IsDerivedFrom(candidate, _modularVehicleClass, superByClass))
                .ToHashSet();

            var vehicles = new HashSet<ulong>();
            foreach (var header in headers)
            {
                const uint DefaultOrArchetype = 0x10 | 0x20;
                if ((header.Flags & DefaultOrArchetype) == 0 && vehicleClasses.Contains(header.Class))
                    vehicles.Add(header.Object);
            }
            lock (_sourceSync) _globalVehicleActors = vehicles;
        }

        private List<UObjectHeader> ReadGlobalObjectHeaders()
        {
            var objectsPerChunk = Offsets.ObjectsPerChunk;
            var objectItemSize = Offsets.ObjectItemSize;
            var count = ReadInt32(_base + NumElements);
            if (count <= 0 || count > 10_000_000) return new List<UObjectHeader>();

            var chunks = ReadPtr(_base + Offsets.GObjects);
            var objectPointers = new List<ulong>(count);
            for (var chunkIndex = 0; chunkIndex * objectsPerChunk < count; chunkIndex++)
            {
                if (_stop.IsCancellationRequested) break;
                try
                {
                    var chunk = ReadPtr(chunks + (ulong)chunkIndex * 8) & ~0xFUL;
                    if (chunk == 0) continue;
                    var entries = Math.Min(objectsPerChunk, count - chunkIndex * objectsPerChunk);
                    var bytes = ReadExact(_process, chunk, checked((uint)(entries * (long)objectItemSize)));
                    for (var index = 0; index < entries; index++)
                    {
                        var itemOffset = index * (int)objectItemSize + (int)Offsets.ObjectItemObject;
                        var objectPtr = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(itemOffset, 8));
                        if (objectPtr != 0) objectPointers.Add(objectPtr);
            }
            }
                catch { }
            }

            var headers = new List<UObjectHeader>(objectPointers.Count);
            const int batchSize = 512;
            for (var start = 0; start < objectPointers.Count; start += batchSize)
            {
                if (_stop.IsCancellationRequested) break;
                var length = Math.Min(batchSize, objectPointers.Count - start);
                using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                for (var i = 0; i < length; i++)
                    scatter.Prepare(objectPointers[start + i] + Offsets.UObjectFlags, 0x20);
                if (!scatter.Execute()) continue;

                for (var i = 0; i < length; i++)
                {
                    var objectPtr = objectPointers[start + i];
                    var data = scatter.Read(objectPtr + Offsets.UObjectFlags, 0x20);
                    if (data is null || data.Length != 0x20) continue;
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
                    var classPtr = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8, 8));
                    var outer = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24, 8));
                    if (classPtr != 0) headers.Add(new UObjectHeader(objectPtr, flags, classPtr, outer));
                }
                if (!_stop.IsCancellationRequested) Thread.Sleep(1);
            }
            return headers;
        }

        private Dictionary<ulong, ulong> ReadSuperClasses(IReadOnlyList<ulong> classes)
        {
            var result = new Dictionary<ulong, ulong>(classes.Count);
            const int batchSize = 512;
            for (var start = 0; start < classes.Count; start += batchSize)
            {
                var length = Math.Min(batchSize, classes.Count - start);
                using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                for (var i = 0; i < length; i++) scatter.Prepare(classes[start + i] + Offsets.ClassSuper, 8);
                if (!scatter.Execute()) continue;
                for (var i = 0; i < length; i++)
                {
                    var candidate = classes[start + i];
                    var data = scatter.Read(candidate + Offsets.ClassSuper, 8);
                    if (data is not null && data.Length == 8)
                        result[candidate] = BinaryPrimitives.ReadUInt64LittleEndian(data);
                }
            }
            return result;
        }

        private static bool IsDerivedFrom(ulong candidate, ulong target,
            IReadOnlyDictionary<ulong, ulong> superByClass)
        {
            for (var depth = 0; candidate != 0 && depth < 32; depth++)
            {
                if (candidate == target) return true;
                if (!superByClass.TryGetValue(candidate, out candidate)) return false;
            }
            return false;
        }

        private bool TryReadCamera(out CameraState camera)
        {
            camera = default;
            var controller = _cachedController;
            if (controller == 0) controller = ReadLocalController();
            if (controller == 0) return TryReuseRecentCamera(out camera);
            var manager = TryReadPtr(controller + Offsets.CameraManager);
            if (Interlocked.Exchange(ref _cameraDebugPrinted, 1) == 0)
                Console.WriteLine($"ESP camera probe: controller=0x{controller:X}, manager=0x{manager:X}");
            if (manager == 0) return TryReuseRecentCamera(out camera);
            TryReadControlYaw(controller, out var controlYaw);
            ulong activeActor;
            lock (_sourceSync) activeActor = _activeLocalActor;
            var vehicleView = false;
            if (IsPlausiblePointer(activeActor))
            {
                vehicleView = GetClassInfoForActor(activeActor).IsVehicle;
                if (!vehicleView)
                {
                    lock (_sourceSync)
                        vehicleView = _globalVehicleActors.Contains(activeActor) || _levelVehicleActors.Contains(activeActor);
                }
            }
            _lastVehicleCameraView = vehicleView;
            var bases = Offsets.CameraCacheCandidates.Select(x => manager + x).ToArray();
            using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
            foreach (var address in bases) scatter.Prepare(address, 0x60);
            var scatterOk = scatter.Execute();
            var valid = new List<(CameraState Camera, int Index, float Stamp)>();
            for (var i = 0; i < bases.Length; i++)
            {
                // Camera records are tiny; use a fresh direct read so a
                // successful scatter transfer cannot leave a torn 0x60-byte
                // record that passes the length check but fails validation.
                byte[]? raw = null;
                try { raw = ReadExact(_process, bases[i], 0x60); } catch { }
                if (raw is not byte[] data || data.Length != 0x60) continue;
                // FCameraCacheEntry/FTViewTarget embeds POV at +0x10.
                var p = (int)Offsets.CameraPov;
                var candidate = new CameraState(
                    new Vector3((float)BitConverter.ToDouble(data, p), (float)BitConverter.ToDouble(data, p + 8), (float)BitConverter.ToDouble(data, p + 16)),
                    new Vector3((float)BitConverter.ToDouble(data, p + 24), (float)BitConverter.ToDouble(data, p + 32), (float)BitConverter.ToDouble(data, p + 40)),
                    BitConverter.ToSingle(data, p + 48));
                // High-magnification optics can drive FOV below 20 degrees.
                // Keep the valid camera range down to 1 degree; Project()
                // still rejects non-positive/degenerate values.
                if (float.IsFinite(candidate.Fov) && candidate.Fov > 1 && candidate.Fov < 160 &&
                    float.IsFinite(candidate.Location.X) && float.IsFinite(candidate.Location.Y) &&
                    float.IsFinite(candidate.Location.Z) && candidate.Location.Length() > 1 &&
                    candidate.Location.Length() < 1e8f &&
                    // A pitch of exactly +/-90 with yaw reset to zero is a
                    // stale/uninitialized camera record observed while ADS;
                    // it projects every actor off-screen. Reject that record
                    // and keep the last continuous camera instead.
                    MathF.Abs(candidate.Rotation.X) < 89.5f &&
                    MathF.Abs(candidate.Rotation.Y) <= 360 &&
                    MathF.Abs(candidate.Rotation.Z) <= 360)
                {
                    var stamp = BitConverter.ToSingle(data, 0);
                    valid.Add((candidate, i, float.IsFinite(stamp) ? stamp : 0));
                }
            }
            if (valid.Count == 0)
            {
                Console.WriteLine("ESP camera probe: no validated candidates");
                // The cache can be read while the game is updating it and
                // briefly fail the strict candidate checks. The SDK's
                // CurrentCameraCachePrivate (+0x1560) remains the canonical
                // source; accept one direct sample before falling back.
                try
                {
                    var raw = ReadExact(_process, manager + Offsets.CurrentCameraCache, 0x60);
                    var p = (int)Offsets.CameraPov;
                    var direct = new CameraState(
                        new Vector3((float)BitConverter.ToDouble(raw, p), (float)BitConverter.ToDouble(raw, p + 8), (float)BitConverter.ToDouble(raw, p + 16)),
                        new Vector3((float)BitConverter.ToDouble(raw, p + 24), (float)BitConverter.ToDouble(raw, p + 32), (float)BitConverter.ToDouble(raw, p + 40)),
                        BitConverter.ToSingle(raw, p + 48));
                    if (float.IsFinite(direct.Fov) && direct.Fov > 1 && direct.Fov < 160 &&
                        direct.Location.Length() > 1 && direct.Location.Length() < 1e8f)
                    {
                        camera = direct;
                        _lastCameraState = direct;
                        _lastCameraValidUtc = DateTime.UtcNow;
                        _hasLastCamera = true;
                        _lastCameraIndex = 2;
                        return true;
                    }
                }
                catch { }
                return TryReuseRecentCamera(out camera);
            }
            // Select the cache entry by continuity, with CurrentCameraCache
            // (index 2) as the stable tie-breaker. Pending/ViewTarget entries
            // can retain an old low FOV while the player is not actually ADS;
            // choosing the lowest FOV unconditionally makes boxes explode or
            // drift. A real ADS transition remains eligible when its location,
            // rotation and FOV change continuously from the previous frame.
            var reference = _hasLastCamera ? _lastCameraState : default;
            var selected = valid
                .Select(v =>
                {
                    var score = v.Index == 2 ? 8 : 0;
                    if (_hasLastCamera && IsCameraNear(v.Camera, reference)) score += 4;
                    if (_hasLastCamera && v.Index == _lastCameraIndex) score += 2;
                    if (_hasLastCamera && MathF.Abs(v.Camera.Fov - reference.Fov) <= 25f) score += 2;
                    // Reject implausible one-frame low-FOV jumps from stale
                    // PendingViewTarget data; genuine ADS changes smoothly.
                    if (_hasLastCamera && v.Camera.Fov < 30f && reference.Fov > 45f &&
                        MathF.Abs(v.Camera.Fov - reference.Fov) > 25f) score -= 6;
                    return (Value: v, Score: score);
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Value.Stamp)
                .First().Value;
            // Reject cache records that jump too far in one frame.  During
            // X6 ADS the camera cache can briefly contain a valid-looking
            // record from another view target; its rotation/location projects
            // every actor with an enormous box.  Keep the last camera until
            // the new view converges.
            if (_hasLastCamera)
            {
                var locationJump = Vector3.Distance(selected.Camera.Location, _lastCameraState.Location);
                var pitchJump = WrappedAngleDelta(selected.Camera.Rotation.X, _lastCameraState.Rotation.X);
                var yawJump = WrappedAngleDelta(selected.Camera.Rotation.Y, _lastCameraState.Rotation.Y);
                var rollJump = WrappedAngleDelta(selected.Camera.Rotation.Z, _lastCameraState.Rotation.Z);
                var fovJump = MathF.Abs(selected.Camera.Fov - _lastCameraState.Fov);
                if (locationJump > 25000f || pitchJump > 65f || yawJump > 150f ||
                    rollJump > 45f || (fovJump > 45f && locationJump > 5000f))
                {
                    selected = (new CameraState(_lastCameraState.Location, _lastCameraState.Rotation, _lastCameraState.Fov),
                        _lastCameraIndex, 0f);
                }
            }
            // Camera cache fields are written by the game on another thread;
            // during optic transitions a torn frame can briefly combine the
            // normal FOV (100) with the ADS FOV (12-18). Require two
            // consecutive samples before accepting a large FOV jump, while
            // still switching promptly once the optic settles.
            // During a high-power optic transition all camera cache records
            // can legitimately switch to the same low FOV (the 6x sight
            // commonly reports ~12 degrees).  Such a unanimous sample is
            // stronger evidence than a single stale PendingViewTarget entry
            // and must be accepted immediately; otherwise the two-sample
            // hysteresis keeps projection at 60/100 FOV and looks unzoomed.
            var unanimousLowFov = selected.Camera.Fov < 30f &&
                                  valid.Count >= 2 && valid.All(v => v.Camera.Fov < 30f);
            if (_hasLastCamera && selected.Index == _lastCameraIndex &&
                MathF.Abs(selected.Camera.Fov - _lastCameraState.Fov) > 25f &&
                !unanimousLowFov)
            {
                if (_pendingCameraIndex == selected.Index &&
                    MathF.Abs(_pendingCamera.Fov - selected.Camera.Fov) < 2f)
                    _pendingCameraStreak++;
                else
                {
                    _pendingCamera = selected.Camera;
                    _pendingCameraIndex = selected.Index;
                    _pendingCameraStreak = 1;
                }
                if (_pendingCameraStreak < 2)
                {
                    selected = (new CameraState(_lastCameraState.Location, _lastCameraState.Rotation, _lastCameraState.Fov), _lastCameraIndex, 0f);
                }
                else
                {
                    _pendingCameraIndex = -1;
                    _pendingCameraStreak = 0;
                }
            }
            else
            {
                _pendingCameraIndex = -1;
                _pendingCameraStreak = 0;
            }
            // CameraCache in Wardogs reports the gameplay FOV (often 60/100)
            // even while a high-power optic is active.  Resolve the runtime
            // ADS multiplier from the local weapon behavior component and
            // apply it to the projection camera only when the value is
            // plausible and the player is in an aiming view.
            var selectedCamera = selected.Camera;
            // The camera cache can retain a stale yaw while the controller
            // rotation is updated every frame. Use the live control yaw for
            // projection whenever it is valid so ESP follows view movement.
            // In a vehicle the camera can be offset from the body/turret. The
            // controller yaw then differs legitimately from the camera cache;
            // overriding it makes every box slide while the vehicle turns.
            var cameraYawDelta = WrappedAngleDelta(controlYaw, selectedCamera.Rotation.Y);
            if (float.IsFinite(controlYaw) && MathF.Abs(controlYaw) <= 360f &&
                (!vehicleView || cameraYawDelta <= 20f))
            {
                selectedCamera = new CameraState(selectedCamera.Location,
                    new Vector3(selectedCamera.Rotation.X, controlYaw, selectedCamera.Rotation.Z),
                    selectedCamera.Fov);
            }
            // A scope transition in this build briefly publishes the real
            // optical FOV (the 6x optic reports about 12 degrees), then the
            // same cache alternates back to 60/100 while the scope remains
            // active. Keep the last verified optical FOV through that torn
            // interval so boxes do not expand and drift between projections.
            var candidateOpticFov = valid
                .Select(v => v.Camera.Fov)
                .Where(f => f >= 3f && f <= 25f)
                .DefaultIfEmpty(selectedCamera.Fov)
                .Min();
            if (candidateOpticFov >= 3f && candidateOpticFov <= 25f)
            {
                _lastObservedOpticFov = candidateOpticFov;
                _lastObservedOpticUtc = DateTime.UtcNow;
            }
            else if (_lastObservedOpticFov >= 3f &&
                     DateTime.UtcNow - _lastObservedOpticUtc < TimeSpan.FromMilliseconds(900) &&
                     selectedCamera.Fov >= 45f)
            {
                selectedCamera = new CameraState(selectedCamera.Location,
                    selectedCamera.Rotation, _lastObservedOpticFov);
            }
            if (TryReadAdsFovMultiplier(controller, selectedCamera.Fov,
                    out var adsFov, out var adsDiag) &&
                float.IsFinite(adsFov) && adsFov >= 3f && adsFov <= selectedCamera.Fov + 0.5f)
            {
                selectedCamera = new CameraState(selectedCamera.Location,
                    selectedCamera.Rotation, adsFov);
            }
            camera = selectedCamera;
            _lastCameraIndex = selected.Index;
            if (float.IsFinite(controlYaw)) { _lastControlYaw = controlYaw; _hasControlYaw = true; }
            // Keep the unmodified cache value for continuity/jump filtering;
            // the ADS-adjusted camera above is only for this frame's screen
            // projection and must not poison the next cache comparison.
            _lastCameraState = selected.Camera;
            _lastCameraValidUtc = DateTime.UtcNow;
            _hasLastCamera = true;
            if (DateTime.UtcNow - _lastCameraDiagUtc > TimeSpan.FromSeconds(2))
            {
                _lastCameraDiagUtc = DateTime.UtcNow;
                var fovSummary = string.Join(",", valid.Select(v => $"{v.Index}:{v.Camera.Fov:0.0}"));
                LogMessage(3, $"ESP camera source={selected.Index},loc={camera.Location.X:0},{camera.Location.Y:0},{camera.Location.Z:0},rot={camera.Rotation.X:0},{camera.Rotation.Y:0},{camera.Rotation.Z:0},controlYaw={(float.IsFinite(controlYaw) ? controlYaw : float.NaN):0.0},fov={camera.Fov:0.0},candidates=[{fovSummary}],valid={valid.Count},ads={adsDiag}");
            }
            return true;
        }

        private bool TryReadAdsFovMultiplier(ulong controller, float cameraFov,
            out float effectiveFov, out string diagnostic)
        {
            effectiveFov = cameraFov;
            diagnostic = "component=0x0,aim=0,m=1.00,base=1.00,effective=none";
            try
            {
                var pawns = new List<ulong>();
                lock (_sourceSync)
                {
                    if (IsPlausiblePointer(_lastLocalPlayerPawn)) pawns.Add(_lastLocalPlayerPawn);
                    if (IsPlausiblePointer(_localPawn)) pawns.Add(_localPawn);
                    if (IsPlausiblePointer(_activeLocalActor)) pawns.Add(_activeLocalActor);
                    // The controller can temporarily expose only a vehicle
                    // pawn (mortar/weapon view). Include every replicated
                    // character so the local weapon component is still found
                    // when the cached character pointer is unavailable.
                    pawns.AddRange(_playerSourceActors.Where(IsPlausiblePointer));
                }
                if (IsPlausiblePointer(controller))
                    foreach (var offset in new[] { Offsets.ControllerPawnCurrent, Offsets.ControllerPawn, Offsets.ControllerPawnAlt })
                    {
                        var pawn = TryReadPtr(controller + offset);
                        if (IsPlausiblePointer(pawn)) pawns.Add(pawn);
                    }
                foreach (var pawn in pawns.Distinct().OrderBy(p => ActorCameraDistance(p, _lastCameraState.Location)))
                {
                    var component = TryFindWeaponBehaviorComponent(pawn);
                    if (!IsPlausiblePointer(component)) continue;
                    using var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
                    scatter.Prepare(component + Offsets.WeaponAdsMultiplier, 4);
                    scatter.Prepare(component + Offsets.WeaponAdsBaseMultiplier, 4);
                    scatter.Prepare(component + Offsets.WeaponAimingHint, 1);
                    scatter.Prepare(pawn + Offsets.CharacterAimingAlpha, 1);
                    if (!scatter.Execute()) continue;
                    var currentBytes = scatter.Read(component + Offsets.WeaponAdsMultiplier, 4);
                    var baseBytes = scatter.Read(component + Offsets.WeaponAdsBaseMultiplier, 4);
                    var aimBytes = scatter.Read(component + Offsets.WeaponAimingHint, 1);
                    var characterAimBytes = scatter.Read(pawn + Offsets.CharacterAimingAlpha, 1);
                    if (currentBytes is null || baseBytes is null || aimBytes is null) continue;
                    var current = BitConverter.ToSingle(currentBytes, 0);
                    var @base = BitConverter.ToSingle(baseBytes, 0);
                    var aiming = aimBytes[0] != 0 ||
                                 (characterAimBytes is { Length: > 0 } && characterAimBytes[0] != 0);
                    if (!float.IsFinite(current) || !float.IsFinite(@base) ||
                        current <= 0.01f || current > 32f || @base <= 0.01f || @base > 32f)
                        continue;
                    // The runtime slots are current and base FOV factors,
                    // not two independent zoom stages.  For the tested 6x
                    // sight they read 10 and 2, so the effective optic factor
                    // is current/base (5x), rather than current (10x).
                    var currentFactor = current >= 1.05f ? current : 1f / current;
                    var baseFactor = @base >= 1.05f ? @base : 1f / @base;
                    var zoom = currentFactor;
                    if (baseFactor > 1.05f && currentFactor > 1.05f)
                        zoom = currentFactor / baseFactor;
                    zoom = Math.Clamp(zoom, 1f, 16f);
                    // Hip-fire keeps a multiplier of one.  Require either
                    // the component aiming hint or the narrowed gameplay FOV
                    // before applying a high-power zoom, preventing stale
                    // sight data from scaling normal-view ESP boxes.
                    var recentOpticSample = _lastObservedOpticFov >= 3f &&
                        DateTime.UtcNow - _lastObservedOpticUtc < TimeSpan.FromMilliseconds(900);
                    // Rangefinders and low-power optics publish their own
                    // optical FOV in the 26-46 degree range. Their component
                    // factors remain at the weapon's high-power defaults
                    // (for example 10/2), so applying that factor again makes
                    // the view jump to the 8 degree clamp. Only scale a
                    // normal gameplay FOV; already-narrow optical FOV values
                    // are consumed unchanged below.
                    var gameplayFov = cameraFov >= 50f;
                    var active = zoom >= 1.15f && gameplayFov &&
                        (aiming || cameraFov <= 85f || recentOpticSample);
                    // CameraCache already contains the optical FOV for a
                    // settled scope (12-18 degrees). Apply the multiplier
                    // only to the normal gameplay FOV; this prevents the
                    // previous 10x second scaling and oversized boxes.
                    var alreadyOptic = cameraFov <= 25f;
                    var candidateFov = active && !alreadyOptic
                        ? Math.Clamp(cameraFov / zoom, 8f, cameraFov)
                        : cameraFov;
                    diagnostic = $"component=0x{component:X},aim={(aiming ? 1 : 0)},m={current:0.###},base={@base:0.###},zoom={zoom:0.###},effective={(active ? candidateFov.ToString("0.0") : "none")}";
                    if (DateTime.UtcNow - _lastAdsDiagUtc > TimeSpan.FromSeconds(1))
                    {
                        _lastAdsDiagUtc = DateTime.UtcNow;
                        LogMessage(3, $"ESP ADS {diagnostic},cameraFov={cameraFov:0.0}");
                    }
                    if (!active || alreadyOptic) return false;
                    _lastAdsMultiplier = zoom; _hasAdsMultiplier = true;
                    effectiveFov = candidateFov;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private ulong TryFindWeaponBehaviorComponent(ulong pawn)
        {
            // The SDK dump has an adjacent pair whose reflected names/types
            // are swapped by the obfuscator. Probe both slots and prefer the
            // object whose runtime class identifies WeaponBehavior.
            ulong fallback = 0;
            foreach (var offset in new[] { Offsets.WeaponBehaviorComponent, 0x778UL })
            {
                var direct = TryReadPtr(pawn + offset);
                if (!IsPlausiblePointer(direct)) continue;
                var className = TryResolveObjectClassName(direct);
                if (className.Contains("WeaponBehavior", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("WeaponBehaviour", StringComparison.OrdinalIgnoreCase))
                {
                    return direct;
                }
                // Keep the SDK-confirmed 0x770 slot usable when the stripped
                // name table cannot resolve the class on this build.
                if (offset == Offsets.WeaponBehaviorComponent) fallback = direct;
            }
            if (IsPlausiblePointer(fallback)) return fallback;
            // Some streamed character subclasses insert components before the
            // reflected member. Probe the component-sized region and accept
            // only an object whose class name identifies the weapon behavior.
            if (DateTime.UtcNow - _lastAdsComponentScanUtc < TimeSpan.FromMilliseconds(120))
                return 0;
            _lastAdsComponentScanUtc = DateTime.UtcNow;
            for (ulong offset = 0x600; offset <= 0x900; offset += 8)
            {
                var candidate = TryReadPtr(pawn + offset);
                if (!IsPlausiblePointer(candidate)) continue;
                try
                {
                    var cls = TryReadPtr(candidate + Offsets.UObjectClass);
                    if (!IsPlausiblePointer(cls)) continue;
                    var name = _names.Resolve(ReadUInt32(cls + Offsets.ClassNameId));
                    if (name.Contains("WeaponBehavior", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("WeaponBehaviour", StringComparison.OrdinalIgnoreCase))
                    {
                        LogMessage(3, $"ESP ADS component scan pawn=0x{pawn:X},offset=0x{offset:X},component=0x{candidate:X},class={name}");
                        return candidate;
                    }
                }
                catch { }
            }
            return 0;
        }

        private string TryResolveObjectClassName(ulong objectAddress)
        {
            try
            {
                var cls = TryReadPtr(objectAddress + Offsets.UObjectClass);
                if (!IsPlausiblePointer(cls)) return string.Empty;
                return _names.Resolve(ReadUInt32(cls + Offsets.ClassNameId));
            }
            catch { return string.Empty; }
        }

        private float ActorCameraDistance(ulong actor, Vector3 cameraLocation)
        {
            try
            {
                var root = TryReadPtr(actor + Offsets.ActorRootComponent);
                if (!IsPlausiblePointer(root)) return float.MaxValue;
                var bytes = ReadExact(_process, root + Offsets.SceneRelativeLocation, 24);
                if (bytes is null || bytes.Length != 24) return float.MaxValue;
                var p = new Vector3((float)BitConverter.ToDouble(bytes, 0),
                    (float)BitConverter.ToDouble(bytes, 8),
                    (float)BitConverter.ToDouble(bytes, 16));
                var distance = Vector3.Distance(cameraLocation, p);
                return float.IsFinite(distance) ? distance : float.MaxValue;
            }
            catch { return float.MaxValue; }
        }

        private ActorClassInfo GetClassInfoForActor(ulong actor)
        {
            try
            {
                var cls = TryReadPtr(actor + Offsets.ActorClass);
                return cls == 0 ? default : GetClassInfo(cls);
            }
            catch { return default; }
        }

        private bool TryReadControlYaw(ulong controller, out float yaw)
        {
            yaw = float.NaN;
            try
            {
                var bytes = ReadExact(_process, controller + Offsets.ControllerControlRotation, 24);
                var candidate = (float)BitConverter.ToDouble(bytes, 8);
                if (float.IsFinite(candidate) && MathF.Abs(candidate) <= 360f)
                {
                    yaw = candidate;
                    return true;
                }
                // Transitional builds store FRotator as three 32-bit floats.
                candidate = BitConverter.ToSingle(bytes, 4);
                if (float.IsFinite(candidate) && MathF.Abs(candidate) <= 360f)
                {
                    yaw = candidate;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private bool TryReuseRecentCamera(out CameraState camera)
        {
            if (_hasLastCamera && DateTime.UtcNow - _lastCameraValidUtc < TimeSpan.FromMilliseconds(250))
            {
                camera = _lastCameraState;
                return true;
            }
            camera = default;
            return false;
        }

        private static bool IsCameraNear(CameraState candidate, CameraState reference)
        {
            if (!float.IsFinite(reference.Fov) || reference.Fov <= 0) return true;
            var locationDelta = Vector3.Distance(candidate.Location, reference.Location);
            var pitchDelta = WrappedAngleDelta(candidate.Rotation.X, reference.Rotation.X);
            var yawDelta = WrappedAngleDelta(candidate.Rotation.Y, reference.Rotation.Y);
            var rollDelta = WrappedAngleDelta(candidate.Rotation.Z, reference.Rotation.Z);
            return locationDelta <= 10000f && MathF.Max(pitchDelta, MathF.Max(yawDelta, rollDelta)) <= 180f;
        }

        private static float WrappedAngleDelta(float a, float b)
        {
            var delta = MathF.Abs(a - b) % 360f;
            return delta > 180f ? 360f - delta : delta;
        }

        private ulong ReadLocalController()
        {
            // UWorld owns the active game instance in this SDK.  GEngine is
            // not initialized in the current client build, so use it only as
            // a compatibility fallback for older/transitional builds.
            var gameInstances = new List<ulong>(3);
            var world = TryReadPtr(_base + GWorld);
            if (world != 0)
            {
                var owningGameInstance = TryReadPtr(world + Offsets.GameInstance);
                if (owningGameInstance != 0) gameInstances.Add(owningGameInstance);
            }

            var engine = TryReadPtr(_base + GEngine);
            if (engine != 0)
            {
                foreach (var gameInstanceOffset in new[] { Offsets.GameInstance, Offsets.GameInstanceAlt })
                {
                    var gameInstance = TryReadPtr(engine + gameInstanceOffset);
                    if (gameInstance != 0 && !gameInstances.Contains(gameInstance))
                        gameInstances.Add(gameInstance);
                }
            }

            foreach (var gameInstance in gameInstances)
            {
                // LocalPlayers is a TArray<ULocalPlayer*>. Its first qword is
                // already the ULocalPlayer object; do not dereference it a
                // second time before reading UPlayer::PlayerController.
                var localPlayersData = TryReadPtr(gameInstance + Offsets.LocalPlayers);
                if (localPlayersData == 0) continue;
                var localPlayersCount = ReadInt32(gameInstance + Offsets.LocalPlayers + 8);
                if (localPlayersCount <= 0 || localPlayersCount > 16) localPlayersCount = 1;
                ulong fallback = 0;
                for (var i = 0; i < localPlayersCount; i++)
                {
                    var local = TryReadPtr(localPlayersData + (ulong)i * 8);
                    if (local == 0) continue;
                    var controller = TryReadPtr(local + Offsets.LocalPlayerController);
                    if (controller == 0) continue;
                    fallback = fallback == 0 ? controller : fallback;
                    if (TryReadPtr(controller + Offsets.CameraManager) != 0)
                        return controller;
                }
                if (fallback != 0) return fallback;
            }
            return 0;
        }

        private static bool Project(CameraState c, Vector3 world, Size viewport, out PointF screen)
        {
            var delta = world - c.Location;
            var pitch = c.Rotation.X * Math.PI / 180; var yaw = c.Rotation.Y * Math.PI / 180;
            var sp = Math.Sin(pitch); var cp = Math.Cos(pitch); var sy = Math.Sin(yaw); var cy = Math.Cos(yaw);
            var forward = new Vector3((float)(cp * cy), (float)(cp * sy), (float)sp);
            var right = new Vector3((float)-sy, (float)cy, 0); var up = Vector3.Cross(forward, right);
            var z = Vector3.Dot(delta, forward); var x = Vector3.Dot(delta, right); var y = Vector3.Dot(delta, up);
            // Never mirror points behind the camera.  The previous fallback
            // negated x/y and replaced z with Abs(z), which mathematically
            // reflected actors behind the view into the forward frustum and
            // made a player's back-side box appear in front of the camera.
            if (z <= 1)
            {
                screen = default;
                return false;
            }
            if (!float.IsFinite(c.Fov) || c.Fov <= 1 || c.Fov >= 179)
            {
                screen = default;
                return false;
            }
            var f = (float)(viewport.Width / (2 * Math.Tan(c.Fov * Math.PI / 360)));
            screen = new PointF(viewport.Width / 2f + x * f / z, viewport.Height / 2f - y * f / z);
            // Keep projected extremities outside the viewport; zoomed optics
            // routinely push a player's head/feet beyond the screen. GDI
            // clips the final box while TryBuildBox applies a broad guard.
            return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
        }

        private Size GetViewport()
        {
            try { return Screen.PrimaryScreen?.Bounds.Size ?? new Size(1920, 1080); } catch { return new Size(1920, 1080); }
        }
        private ulong ReadUObjectByIndex(int index)
        {
            var objectsPerChunk = Offsets.ObjectsPerChunk;
            var objectItemSize = Offsets.ObjectItemSize;
            var chunks = ReadPtr(_base + Offsets.GObjects);
            var taggedChunk = ReadPtr(chunks + (ulong)(index / objectsPerChunk) * 8);
            var chunk = taggedChunk & ~0xFUL;
            if (chunk == 0) return 0;
            return ReadPtr(chunk + (ulong)(index % objectsPerChunk) * objectItemSize + Offsets.ObjectItemObject);
        }
        private ulong ReadPtr(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(_process, address, 8));
        private uint ReadUInt32(ulong address) => BinaryPrimitives.ReadUInt32LittleEndian(ReadExact(_process, address, 4));
        private byte ReadByte(ulong address) => ReadExact(_process, address, 1)[0];
        private int ReadInt32(ulong address) => BinaryPrimitives.ReadInt32LittleEndian(ReadExact(_process, address, 4));
        private float ReadFloat(ulong address) => BitConverter.ToSingle(ReadExact(_process, address, 4));
        private Vector3 ReadVector(ulong address) => new((float)BitConverter.ToDouble(ReadExact(_process, address, 8)), (float)BitConverter.ToDouble(ReadExact(_process, address + 8, 8)), (float)BitConverter.ToDouble(ReadExact(_process, address + 16, 8)));
        private Vector3 ReadRotator(ulong address) => ReadVector(address);
        public void Dispose()
        {
            _stop.Cancel();
            _thread?.Join(1000);
            _sourceThread?.Join(1000);
            _vehicleThread?.Join(2000);
            _dmaGate.Dispose();
        }
    }

    private readonly record struct CameraState(Vector3 Location, Vector3 Rotation, float Fov);
    private readonly record struct ActorClassInfo(string Name, bool IsPlayer, bool IsVehicle);
    private readonly record struct ActorIdentity(ulong Actor, ulong Class, ulong Root);
    private readonly record struct ActorFrameData(ulong Actor, ulong Class, Vector3 Location, ulong Mesh, float Yaw, string Weapon);
    private readonly record struct UObjectHeader(ulong Object, uint Flags, ulong Class, ulong Outer);
    private readonly record struct FStringHeader(ulong Pointer, int Length);
    private readonly record struct PlayerVisualMeta(int Team, ulong FactionKey, string FactionName,
        bool Downed, float Health, string Name);
    private readonly record struct RadarItem(ulong Actor, Vector3 WorldPosition, float Yaw, bool IsLocal,
        bool IsPlayer, bool Friendly, bool TeamKnown, bool Downed, float Health, string Name, string Weapon);
    private readonly record struct BoneLine(PointF A, PointF B);
    private readonly record struct ComponentTransform(Quaternion Rotation, Vector3 Translation, Vector3 Scale, bool Valid);
    private sealed record DynamicBoneMap(ulong Asset, int Count, int[] Parents,
        Dictionary<string, int> Names, (int A, int B)[] Edges, int[] SampleIndices);
    private readonly record struct EspItem(ulong Actor, Vector3 WorldPosition, float Yaw, bool IsLocal,
        float X, float Y, float Width, float Height,
        bool IsPlayer, bool Friendly, bool TeamKnown, bool Downed, float Health,
        IReadOnlyList<BoneLine> Skeleton,
        string Name, double Distance, string Weapon);
    private readonly record struct EspScan(IReadOnlyList<EspItem> Items, int LevelCount,
        int LevelActorCount, int ActorCount, int PlayerStateCount, int PlayerPawnCount,
        int LocalPawnCount, int GlobalVehicleCount, int PlayerCount, int VehicleCount,
        IReadOnlyList<string> SampleClassNames, string Status)
    {
        public static EspScan Empty(string status) => new(Array.Empty<EspItem>(), 0, 0, 0, 0,
            0, 0, 0, 0, 0, Array.Empty<string>(), status);
    }

    private sealed class NamePool
    {
        private readonly VmmProcess _process; private readonly ulong _pool; private readonly ConcurrentDictionary<uint, string> _cache = new();
        public NamePool(VmmProcess process, ulong pool) { _process = process; _pool = pool; }
        public IReadOnlyDictionary<uint, string> ResolveBatch(IEnumerable<uint> ids)
        {
            var result = new Dictionary<uint, string>();
            var unique = ids.Where(x => x != 0).Distinct().Where(x => !_cache.ContainsKey(x)).ToArray();
            foreach (var id in ids.Distinct()) if (_cache.TryGetValue(id, out var cached)) result[id] = cached;
            if (unique.Length == 0) return result;
            try
            {
                var blockAddresses = unique.Select(id => _pool + Offsets.NamePoolBlocks + (ulong)(id >> 16) * 8).Distinct().ToArray();
                var blocks = new Dictionary<ulong, ulong>();
                using (var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE))
                {
                    foreach (var address in blockAddresses) scatter.Prepare(address, 8);
                    if (!scatter.Execute()) return result;
                    foreach (var address in blockAddresses)
                    {
                        var data = scatter.Read(address, 8);
                        if (data is byte[] bytes && bytes.Length == 8)
                        {
                            var block = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                            if (IsPlausiblePointer(block)) blocks[address] = block;
                        }
                    }
                }
                var entries = new Dictionary<uint, ulong>();
                foreach (var id in unique)
                {
                    var blockAddress = _pool + Offsets.NamePoolBlocks + (ulong)(id >> 16) * 8;
                    if (blocks.TryGetValue(blockAddress, out var block))
                        entries[id] = block + (ulong)(id & 0xFFFF);
                }
                using (var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE))
                {
                    foreach (var entry in entries.Values) scatter.Prepare(entry, 0x40);
                    if (!scatter.Execute()) return result;
                    foreach (var pair in entries)
                    {
                        var data = scatter.Read(pair.Value, 0x40);
                        if (data is not byte[] bytes || bytes.Length != 0x40) continue;
                        var header = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)Offsets.NameEntryHeader, 2));
                        var len = header >> 6;
                        var wide = (header & 1) != 0;
                        if (len <= 0 || len > 256) continue;
                        var byteCount = len * (wide ? 2 : 1);
                        if ((int)Offsets.NameEntryData + byteCount > bytes.Length) continue;
                        var text = (wide ? Encoding.Unicode : Encoding.ASCII)
                            .GetString(bytes, (int)Offsets.NameEntryData, byteCount).TrimEnd('\0');
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        _cache[pair.Key] = text;
                        result[pair.Key] = text;
                    }
                }
            }
            catch { }
            return result;
        }
        public string Resolve(uint id)
        {
            if (id == 0) return "None"; if (_cache.TryGetValue(id, out var cached)) return cached;
            try
            {
                var block = id >> 16; var offset = id & 0xFFFF; var blockPtr = ReadPtr(_pool + Offsets.NamePoolBlocks + (ulong)block * 8);
                // FNameEntryId stores a byte offset within the selected block.
                var entry = blockPtr + (ulong)offset;
                var metadata = ReadExact(_process, entry + Offsets.NameEntryHeader, 4);
                var header = BinaryPrimitives.ReadUInt16LittleEndian(metadata);
                var len = header >> 6; var wide = (header & 1) != 0;
                if (len <= 0 || len > 256) return $"Name_{id:X8}";
                var bytes = ReadExact(_process, entry + Offsets.NameEntryData, (uint)(len * (wide ? 2 : 1)));
                var value = (wide ? Encoding.Unicode : Encoding.ASCII).GetString(bytes).TrimEnd('\0'); _cache[id] = value; return value;
            }
            catch { return $"Name_{id:X8}"; }
        }
        private static bool IsPlausiblePointer(ulong value) => value >= 0x10000 && value <= 0x00007FFFFFFFFFFF;
        private ulong ReadPtr(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(ReadExact(_process, address, 8));
    }

    private sealed class EspOverlay : Form
    {
        private readonly EspTracker _tracker;
        private readonly EspSettings _settings;
        private readonly RadarForm _radar;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _boundsTimer;
        private readonly System.Windows.Forms.Timer _menuKeyTimer;
        private readonly Stopwatch _renderFpsClock = Stopwatch.StartNew();
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private long _lastPresentedTicks;
        private int _renderFrameCount;
        private double _renderFps;
        private ImGuiMenuForm? _menu;
        private bool _insertWasDown;
        private bool _showImGuiMenu;
        private List<Vector2>? _previewDots;
        private GraphicsDevice? _graphics;
        private ImGuiRenderer? _imguiRenderer;
        private IntPtr _imguiContext;
        private CommandList? _commandList;
        private bool _dxReady;
        private bool _dxErrorLogged;
        private int _imguiTab;
        private bool _idleRendering;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
        private const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
        private const int VK_INSERT = 0x2D;
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

        public EspOverlay(EspTracker tracker, EspSettings settings, RadarForm radar)
        {
            _tracker = tracker;
            _settings = settings;
            _radar = radar;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            TopMost = true;
            BackColor = Color.Black;
            ForeColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            ApplyFullscreenBounds();
            // Raise Windows timer granularity so the 1ms unlimited-mode timer
            // is not quantized to the default ~15.6ms (which appears as a
            // hard 60 FPS ceiling when the radar is open).
            timeBeginPeriod(1);
            // The radar has a separate UI loop now, so the ESP can safely use
            // the idle-driven uncapped renderer again. This timer is only a
            // one-second fallback when the main loop is busy with a message.
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (_, _) => { if (!_idleRendering) RenderDxFrame(); };
            _boundsTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _boundsTimer.Tick += (_, _) => { ApplyFullscreenBounds(); ResizeDx(); };
            _menuKeyTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _menuKeyTimer.Tick += (_, _) =>
            {
                var down = (GetAsyncKeyState(VK_INSERT) & 0x8000) != 0;
                if (down && !_insertWasDown)
                {
                    _showImGuiMenu = !_showImGuiMenu;
                    SetOverlayInteractive(_showImGuiMenu);
                    if (_showImGuiMenu)
                    {
                        // The overlay starts as click-through.  Re-apply the
                        // extended-style change immediately so Windows sends
                        // mouse hit-tests to ImGui instead of the game window.
                        SetForegroundWindow(Handle);
                    }
                }
                _insertWasDown = down;
            };
            Shown += (_, _) => InitializeDx();
            Application.Idle += OnApplicationIdle;
            _timer.Start();
            _boundsTimer.Start();
            _menuKeyTimer.Start();
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        private const int GWL_EXSTYLE = -20;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002,
            SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        private void SetOverlayInteractive(bool interactive)
        {
            if (!IsHandleCreated) return;
            var style = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            style = interactive ? style & ~WS_EX_TRANSPARENT : style | WS_EX_TRANSPARENT;
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private void InitializeDx()
        {
            if (_dxReady || IsDisposed || !IsHandleCreated) return;
            try
            {
                var source = SwapchainSource.CreateWin32(Handle, IntPtr.Zero);
                var width = (uint)Math.Max(1, ClientSize.Width);
                var height = (uint)Math.Max(1, ClientSize.Height);
                var swapchain = new SwapchainDescription(source, width, height, null,
                    syncToVerticalBlank: false, colorSrgb: false);
                _graphics = GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions(false, null, false), swapchain);
                _commandList = _graphics.ResourceFactory.CreateCommandList();
                // ImGuiRenderer owns the ImGui context and starts the first
                // frame in its constructor. Capture that context instead of
                // replacing it with an uninitialized one.
                _imguiRenderer = new ImGuiRenderer(_graphics,
                    _graphics.MainSwapchain.Framebuffer.OutputDescription,
                    (int)width, (int)height);
                _imguiContext = ImGui.GetCurrentContext();
                // The renderer creates a Latin default font first.  Merely
                // appending a CJK font leaves that Latin font active, which
                // turns every Chinese label into '?'.  Make the CJK font the
                // actual default (prefer standalone TTF files; stb_truetype
                // support for TTC collections varies between builds).
                var cjkFont = new[]
                {
                    @"C:\Windows\Fonts\simhei.ttf",
                    @"C:\Windows\Fonts\msyh.ttf",
                    @"C:\Windows\Fonts\msyh.ttc"
                }.FirstOrDefault(File.Exists);
                if (cjkFont is not null)
                {
                    var io = ImGui.GetIO();
                    // Remove the Latin Proggy font created by the renderer so
                    // the CJK font becomes atlas slot 0 and is used by all
                    // menu labels without relying on FontDefault setters.
                    io.Fonts.Clear();
                    var font = io.Fonts.AddFontFromFileTTF(cjkFont, 18f, null, io.Fonts.GetGlyphRangesChineseFull());
                    io.Fonts.Build();
                    _imguiRenderer.RecreateFontDeviceTexture();
                    Console.WriteLine($"ImGui CJK font loaded as default: {cjkFont}");
                }
                else Console.WriteLine("ImGui CJK font missing; install Microsoft YaHei or SimHei.");
                _dxReady = true;
                Console.WriteLine("ESP renderer: ImGui + Direct3D11 (GPU)");
            }
            catch (Exception ex)
            {
                if (!_dxErrorLogged)
                {
                    _dxErrorLogged = true;
                    Console.WriteLine($"ESP DX11 init error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void ResizeDx()
        {
            if (!_dxReady || _graphics is null) return;
            var width = (uint)Math.Max(1, ClientSize.Width);
            var height = (uint)Math.Max(1, ClientSize.Height);
            try
            {
                _graphics.MainSwapchain.Resize(width, height);
                _imguiRenderer?.WindowResized((int)width, (int)height);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ESP DX11 resize error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private sealed class EmptyInputSnapshot : InputSnapshot
        {
            public static readonly EmptyInputSnapshot Instance = new();
            public IReadOnlyList<KeyEvent> KeyEvents => Array.Empty<KeyEvent>();
            public IReadOnlyList<MouseEvent> MouseEvents => Array.Empty<MouseEvent>();
            public IReadOnlyList<char> KeyCharPresses => Array.Empty<char>();
            public Vector2 MousePosition => Vector2.Zero;
            public float WheelDelta => 0;
            public bool IsMouseDown(MouseButton button) => false;
        }

        private static uint ImColor(Color c) => ImGui.ColorConvertFloat4ToU32(
            new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f));

        private static bool Valid(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) &&
                                                 MathF.Abs(p.X) < 100000 && MathF.Abs(p.Y) < 100000;

        private void RenderDxFrame()
        {
            if (!_dxReady || _graphics is null || _imguiRenderer is null || _commandList is null) return;
            var fpsLimit = Volatile.Read(ref _settings.RenderFpsLimit);
            if (fpsLimit > 0)
            {
                var nowTicks = Stopwatch.GetTimestamp();
                var minTicks = Stopwatch.Frequency / Math.Max(1, fpsLimit);
                var previous = Interlocked.Read(ref _lastPresentedTicks);
                if (previous != 0 && nowTicks - previous < minTicks) return;
                Interlocked.Exchange(ref _lastPresentedTicks, nowTicks);
            }
            try
            {
                var dt = (float)Math.Clamp(_frameClock.Elapsed.TotalSeconds, 0.0001, 0.1);
                _frameClock.Restart();
                ImGui.SetCurrentContext(_imguiContext);
                _imguiRenderer.Update(dt, PolledInputSnapshotInstance);
                if (_showImGuiMenu) DrawImGuiMenu();
                var draw = ImGui.GetBackgroundDrawList();
                var snapshot = _tracker.Snapshot;
                foreach (var item in snapshot)
                {
                    var color = item.IsPlayer
                        ? (item.Downed ? Color.FromArgb(_settings.DownedColorArgb) : item.TeamKnown
                            ? Color.FromArgb(item.Friendly ? _settings.FriendlyColorArgb : _settings.EnemyColorArgb)
                            : Color.FromArgb(_settings.UnknownColorArgb))
                        : (item.TeamKnown
                            ? Color.FromArgb(item.Friendly ? _settings.FriendlyVehicleColorArgb : _settings.EnemyVehicleColorArgb)
                            : Color.FromArgb(_settings.UnknownColorArgb));
                    var min = new Vector2(item.X, item.Y);
                    var max = new Vector2(item.X + item.Width, item.Y + item.Height);
                    if (!Valid(min) || !Valid(max) || item.Width <= 0 || item.Height <= 0) continue;
                    var col = ImColor(color);
                    draw.AddRect(min, max, col, 0, ImDrawFlags.None, 1.5f);
                    if (item.IsPlayer && _settings.ShowSkeleton && item.Skeleton.Count > 0)
                    {
                        foreach (var line in item.Skeleton)
                        {
                            var a = new Vector2(line.A.X, line.A.Y);
                            var b = new Vector2(line.B.X, line.B.Y);
                            if (Valid(a) && Valid(b)) draw.AddLine(a, b, col, 1.2f);
                        }
                    }
                    if (item.IsPlayer && _settings.ShowHealthBar && item.Health >= 0)
                    {
                        var barHeight = Math.Clamp(item.Height, 2, 32767);
                        var filled = barHeight * Math.Clamp(item.Health, 0, 100) / 100f;
                        var barMin = new Vector2(item.X - 7, item.Y);
                        var barMax = new Vector2(item.X - 3, item.Y + barHeight);
                        draw.AddRectFilled(barMin, barMax, ImColor(Color.FromArgb(190, 20, 20, 20)));
                        draw.AddRectFilled(new Vector2(barMin.X, barMax.Y - filled),
                            barMax, ImColor(item.Health <= 25 ? Color.Red : item.Health <= 60 ? Color.Yellow : Color.LimeGreen));
                    }
                    var text = _settings.ShowNames ? item.Name : string.Empty;
                    if (_settings.ShowWeapons && !string.IsNullOrWhiteSpace(item.Weapon) &&
                        !text.Contains(item.Weapon, StringComparison.OrdinalIgnoreCase))
                        text = string.IsNullOrEmpty(text) ? $"[{item.Weapon}]" : $"{text} [{item.Weapon}]";
                    if (_settings.ShowDistance)
                        text = string.IsNullOrEmpty(text) ? $"[{item.Distance:0}m]" : $"{text} [{item.Distance:0}m]";
                    if (!string.IsNullOrEmpty(text))
                    {
                        var pos = new Vector2(item.X + 2, item.Y - 19);
                        if (Valid(pos))
                        {
                            var textSize = ImGui.CalcTextSize(text);
                            draw.AddRectFilled(pos - new Vector2(2, 1),
                                pos + textSize + new Vector2(2, 1), ImColor(Color.Black));
                            draw.AddText(pos, col, text);
                        }
                    }
                }
                DrawEspMiniRadar(draw);
                _renderFrameCount++;
                if (_renderFpsClock.Elapsed >= TimeSpan.FromMilliseconds(500))
                {
                    _renderFps = _renderFrameCount / Math.Max(_renderFpsClock.Elapsed.TotalSeconds, 0.001);
                    _renderFrameCount = 0;
                    _renderFpsClock.Restart();
                }
                var fpsText = $"ESP FPS: {_renderFps:0.0} | DMA 读取: {_tracker.ReadFps:0.0} FPS / {_tracker.ReadAverageMs:0.00} ms";
                var fpsPos = new Vector2(14, 12);
                var fpsSize = ImGui.CalcTextSize(fpsText);
                draw.AddRectFilled(new Vector2(8, 8), fpsPos + fpsSize + new Vector2(6, 6), ImColor(Color.FromArgb(220, 0, 0, 0)));
                draw.AddText(fpsPos, ImColor(Color.Lime), fpsText);
                _commandList.Begin();
                _commandList.SetFramebuffer(_graphics.MainSwapchain.Framebuffer);
                _commandList.ClearColorTarget(0, RgbaFloat.Black);
                _imguiRenderer.Render(_graphics, _commandList);
                _commandList.End();
                _graphics.SubmitCommands(_commandList);
                _graphics.SwapBuffers();
            }
            catch (Exception ex)
            {
                if (!_dxErrorLogged)
                {
                    _dxErrorLogged = true;
                    Console.WriteLine($"ESP DX11 render error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>Draws a compact, player-only circular radar in the ESP overlay.</summary>
        private void DrawEspMiniRadar(ImDrawListPtr draw)
        {
            if (!_settings.EspMiniRadarEnabled) return;
            var radius = Math.Clamp(_settings.EspMiniRadarRadius, 80f, 320f);
            var range = Math.Max(10000f, _settings.EspMiniRadarRange);
            var width = Math.Max(640, ClientSize.Width);
            var center = new Vector2(width - radius - 28f, radius + 78f);
            var ring = ImColor(Color.FromArgb(220, 95, 180, 165));
            var grid = ImColor(Color.FromArgb(95, 100, 180, 165));
            draw.AddCircleFilled(center, radius, ImColor(Color.FromArgb(150, 5, 14, 20)));
            draw.AddCircle(center, radius, ring, 64, 2f);
            draw.AddCircle(center, radius * 0.5f, grid, 48, 1f);
            draw.AddLine(center - new Vector2(radius, 0), center + new Vector2(radius, 0), grid, 1f);
            draw.AddLine(center - new Vector2(0, radius), center + new Vector2(0, radius), grid, 1f);
            draw.AddText(center + new Vector2(-radius, -radius - 20), ring,
                $"PLAYER RADAR  {range / 100f:0}m");

            var snapshot = _tracker.RadarSnapshot;
            RadarItem local = default;
            var haveLocal = false;
            for (var i = 0; i < snapshot.Count; i++)
            {
                var candidate = snapshot[i];
                if (candidate.IsLocal && candidate.IsPlayer)
                {
                    local = candidate;
                    haveLocal = true;
                    break;
                }
                // When the local pawn is inside a mortar/vehicle, the tracker
                // can expose the local actor as a non-player entry. Keep that
                // entry as a positional anchor while still drawing players only.
                if (!haveLocal && candidate.IsLocal)
                {
                    local = candidate;
                    haveLocal = true;
                }
            }
            if (!haveLocal)
            {
                // Keep the radar frame visible while the game is loading or the
                // local pawn is temporarily replaced by a mortar/vehicle pawn.
                draw.AddCircleFilled(center, 5f, ImColor(Color.Cyan));
                return;
            }

            var localYaw = local.Yaw * MathF.PI / 180f;
            var localForward = new Vector2(MathF.Cos(localYaw), MathF.Sin(localYaw));
            var localRight = new Vector2(-MathF.Sin(localYaw), MathF.Cos(localYaw));
            // Local marker and its heading line (up is the player's forward).
            draw.AddCircleFilled(center, 5f, ImColor(Color.Cyan));
            draw.AddLine(center, center - new Vector2(0, radius * 0.18f), ImColor(Color.Cyan), 2f);

            for (var i = 0; i < snapshot.Count; i++)
            {
                var item = snapshot[i];
                if (!item.IsPlayer || item.IsLocal) continue; // player-only; no vehicles
                var delta = item.WorldPosition - local.WorldPosition;
                var forward = Vector2.Dot(new Vector2(delta.X, delta.Y), localForward);
                var right = Vector2.Dot(new Vector2(delta.X, delta.Y), localRight);
                var distance = MathF.Sqrt(forward * forward + right * right);
                if (!float.IsFinite(distance)) continue;
                var scale = MathF.Min(1f, distance / range);
                var point = center + new Vector2(right / range * radius, -forward / range * radius);
                if (distance > range && distance > 0.01f)
                {
                    var edge = new Vector2(right / distance, -forward / distance) * (radius - 4f);
                    point = center + edge;
                }
                if (item.TeamKnown && item.Friendly && !_settings.EspMiniRadarShowTeammates) continue;
                if (item.TeamKnown && !item.Friendly && !_settings.EspMiniRadarShowEnemies) continue;
                var color = item.TeamKnown
                    ? (item.Friendly ? Color.LimeGreen : Color.Red)
                    : Color.Gray;
                if (item.Downed) color = Color.Orange;
                var col = ImColor(color);
                draw.AddCircleFilled(point, item.Downed ? 4.5f : 4f, col);

                // Heading line uses the same world-to-local basis as the point,
                // so rotating the view rotates every contact consistently.
                var yaw = item.Yaw * MathF.PI / 180f;
                var hForward = MathF.Cos(yaw) * localForward.X + MathF.Sin(yaw) * localForward.Y;
                var hRight = MathF.Cos(yaw) * localRight.X + MathF.Sin(yaw) * localRight.Y;
                var heading = new Vector2(hRight, -hForward);
                if (_settings.EspMiniRadarShowHeading && heading.LengthSquared() > 0.001f)
                    draw.AddLine(point, point + Vector2.Normalize(heading) * 12f, col, 1.5f);
                if (_settings.EspMiniRadarShowNames && !string.IsNullOrWhiteSpace(item.Name))
                    draw.AddText(point + new Vector2(6, -7), col, item.Name);
            }
        }

        private sealed class PolledInputSnapshot : InputSnapshot
        {
            public IReadOnlyList<KeyEvent> KeyEvents => Array.Empty<KeyEvent>();
            public IReadOnlyList<MouseEvent> MouseEvents => Array.Empty<MouseEvent>();
            public IReadOnlyList<char> KeyCharPresses => Array.Empty<char>();
            public Vector2 MousePosition { get { GetCursorPos(out var p); return new Vector2(p.X, p.Y); } }
            public float WheelDelta => 0;
            public bool IsMouseDown(MouseButton button) => button switch
            {
                MouseButton.Left => (GetAsyncKeyState(1) & 0x8000) != 0,
                MouseButton.Right => (GetAsyncKeyState(2) & 0x8000) != 0,
                MouseButton.Middle => (GetAsyncKeyState(4) & 0x8000) != 0,
                _ => false
            };
        }
        private static readonly InputSnapshot PolledInputSnapshotInstance = new PolledInputSnapshot();

        [DllImport("user32.dll")]
        private static extern uint GetQueueStatus(uint flags);
        private const uint QS_ALLINPUT = 0x04FF;

        private void OnApplicationIdle(object? sender, EventArgs e)
        {
            if (_idleRendering || IsDisposed) return;
            _idleRendering = true;
            try
            {
                // Stop the tight loop as soon as a Windows message arrives so
                // INSERT, resizing and ImGui mouse input remain responsive.
                while (!IsDisposed && GetQueueStatus(QS_ALLINPUT) == 0)
                    RenderDxFrame();
            }
            finally { _idleRendering = false; }
        }

        private void DrawImGuiMenu()
        {
            ImGui.SetNextWindowSize(new Vector2(900, 650), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(new Vector2(80, 60), ImGuiCond.FirstUseEver);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.035f, 0.045f, 0.065f, 0.98f));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.055f, 0.065f, 0.09f, 1));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8);
            if (ImGui.Begin("WARDOGS / ESP 控制台", ref _showImGuiMenu, ImGuiWindowFlags.NoCollapse))
            {
                ImGui.BeginChild("sidebar", new Vector2(155, 0), true);
                ImGui.TextColored(new Vector4(0.2f, 0.95f, 0.85f, 1), "设置导航"); ImGui.Separator();
                var tabs = new[] { "ESP 总览", "视觉设置", "外观颜色", "性能设置", "DMA 诊断" };
                for (var i = 0; i < tabs.Length; i++)
                {
                    if (ImGui.Selectable(tabs[i], _imguiTab == i, ImGuiSelectableFlags.None, new Vector2(145, 38)))
                        _imguiTab = i;
                }
                ImGui.EndChild(); ImGui.SameLine();
                ImGui.BeginChild("content", new Vector2(470, 0), true);
                ImGui.TextColored(new Vector4(0.3f, 0.75f, 1, 1), tabs[_imguiTab]); ImGui.Separator();
                if (_imguiTab == 0)
                {
                    if (ImGui.Button("打开 / 显示 2D 雷达", new Vector2(210, 28)) && !_radar.IsDisposed)
                        _radar.Show();
                    ImGui.SameLine(); ImGui.Text("独立窗口"); ImGui.Separator();
                    ImGui.Checkbox("启用 ESP", ref _settings.Enabled);
                    ImGui.Checkbox("显示玩家", ref _settings.ShowPlayers);
                    ImGui.Checkbox("显示队友", ref _settings.ShowTeammates);
                    ImGui.Checkbox("显示敌人", ref _settings.ShowEnemies);
                    ImGui.Separator();
                    ImGui.TextColored(new Vector4(0.3f, 0.75f, 1, 1), "载具");
                    ImGui.Checkbox("显示载具", ref _settings.ShowVehicles);
                    ImGui.Checkbox("显示友方载具", ref _settings.ShowFriendlyVehicles);
                    ImGui.Checkbox("显示敌方载具", ref _settings.ShowEnemyVehicles);
                }
                else if (_imguiTab == 1)
                {
                    ImGui.Separator();
                    ImGui.TextColored(new Vector4(0.3f, 0.95f, 0.85f, 1), "ESP 圆形玩家雷达");
                    ImGui.Checkbox("启用圆形雷达", ref _settings.EspMiniRadarEnabled);
                    ImGui.Checkbox("雷达显示名称", ref _settings.EspMiniRadarShowNames);
                    ImGui.Checkbox("雷达显示朝向线", ref _settings.EspMiniRadarShowHeading);
                    ImGui.Checkbox("雷达显示队友", ref _settings.EspMiniRadarShowTeammates);
                    ImGui.Checkbox("雷达显示敌人", ref _settings.EspMiniRadarShowEnemies);
                    var miniRadius = _settings.EspMiniRadarRadius;
                    if (ImGui.SliderFloat("雷达半径", ref miniRadius, 80f, 320f, "%.0f px"))
                        _settings.EspMiniRadarRadius = miniRadius;
                    var miniRange = _settings.EspMiniRadarRange / 100f;
                    if (ImGui.SliderFloat("显示范围", ref miniRange, 100f, 10000f, "%.0f m"))
                        _settings.EspMiniRadarRange = miniRange * 100f;
                    ImGui.Separator();
                    ImGui.Checkbox("显示玩家骨骼", ref _settings.ShowSkeleton);
                    ImGui.Checkbox("显示手持武器", ref _settings.ShowWeapons);
                    ImGui.Checkbox("显示血条", ref _settings.ShowHealthBar);
                    ImGui.Checkbox("显示名称", ref _settings.ShowNames);
                    ImGui.Checkbox("显示距离", ref _settings.ShowDistance);
                    ImGui.Checkbox("显示倒地玩家", ref _settings.ShowDowned);
                }
                else if (_imguiTab == 2)
                {
                    ImGui.TextWrapped("颜色配置请使用下方的颜色设置窗口。");
                    ImGui.Text("当前颜色配置已从 wardogs-esp.json 读取。");
                }
                else if (_imguiTab == 3)
                {
                    var fps = _settings.RenderFpsLimit;
                    if (ImGui.SliderInt("ESP FPS (0=不限速)", ref fps, 0, 1000)) _settings.RenderFpsLimit = fps;
                    var read = _settings.ReadIntervalMs;
                    if (ImGui.SliderInt("读取间隔 (ms)", ref read, 0, 1000)) _settings.ReadIntervalMs = read;
                    var logLevel = _settings.LogLevel;
                    if (ImGui.SliderInt("日志级别 (0关/1错误/2常规/3详细)", ref logLevel, 0, 3)) _settings.LogLevel = logLevel;
                    ImGui.TextWrapped("菜单开启时，覆盖层会接收鼠标输入；关闭菜单后自动恢复穿透。");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.3f, 0.95f, 0.85f, 1), "DMA / 调度诊断");
                    ImGui.TextWrapped("测试由唯一 DMA 读取线程执行，渲染线程不会直接访问 Vmm。");
                    if (ImGui.Button("执行一次 SG 与顺序读取对比", new Vector2(270, 30)))
                        _tracker.RequestDmaBenchmark();
                    ImGui.Separator();
                    ImGui.TextWrapped(_tracker.DmaDiagnostics);
                    ImGui.Text($"读取 FPS: {_tracker.ReadFps:0.0}   平均帧耗时: {_tracker.ReadAverageMs:0.00} ms");
                    ImGui.Text("判断：sg=单次Scatter耗时；source/vehicle=后台扫描；dmaWait/frameSkip=调度竞争。sg低而frame高时优先看后台扫描。");
                    ImGui.Text("顺序/SG 比值越高，说明批量 Scatter 对减少往返越有效。");
                }
                ImGui.Separator();
                if (ImGui.Button("保存配置", new Vector2(140, 30))) _settings.Save(); ImGui.SameLine();
                if (ImGui.Button("关闭菜单", new Vector2(140, 30)))
                {
                    _showImGuiMenu = false;
                    SetOverlayInteractive(false);
                }
                ImGui.EndChild();
                ImGui.SameLine();
                ImGui.BeginChild("preview", new Vector2(250, 0), true);
                ImGui.TextColored(new Vector4(0.3f, 0.95f, 0.85f, 1), "ESP 预览模型"); ImGui.Separator();
                var previewOrigin = ImGui.GetCursorScreenPos();
                var previewSize = new Vector2(220, 510);
                ImGui.InvisibleButton("preview_canvas", previewSize);
                var previewDraw = ImGui.GetWindowDrawList();
                previewDraw.AddRectFilled(previewOrigin, previewOrigin + previewSize, ImColor(Color.FromArgb(18, 20, 25)));
                _previewDots ??= LoadPreviewDots();
                if (_previewDots.Count > 0)
                    foreach (var dot in _previewDots)
                    {
                        var p = previewOrigin + new Vector2(dot.X * previewSize.X, dot.Y * previewSize.Y);
                        previewDraw.AddCircleFilled(p, 1.1f, ImColor(Color.FromArgb(110, 220, 205)));
                    }
                else previewDraw.AddText(previewOrigin + new Vector2(32, 240), ImColor(Color.Gray), "模型预览不可用");
                ImGui.EndChild();
            }
            ImGui.End(); ImGui.PopStyleVar(); ImGui.PopStyleColor(2);
        }

        private static List<Vector2> LoadPreviewDots()
        {
            const string path = @"I:\FModel-dev\FModel\bin\x64\Debug\net10.0-windows\win-x64\Output\Exports\Wardogs\Content\Characters\Player\_Presets\Lonestar\03\SK\SK_Preset_Lonestar_03.glb";
            try
            {
                if (!File.Exists(path)) return new();
                var model = SharpGLTF.Schema2.ModelRoot.Load(path, new SharpGLTF.Schema2.ReadSettings());
                var points = new List<Vector3>();
                foreach (var mesh in model.LogicalMeshes)
                    foreach (var prim in mesh.Primitives)
                        if (prim.GetVertexAccessor("POSITION") is { } pos) points.AddRange(pos.AsVector3Array());
                if (points.Count == 0) return new();
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                foreach (var p in points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                var size = Vector3.Max(max - min, new Vector3(0.001f));
                var result = new List<Vector2>(Math.Min(points.Count, 4000));
                var step = Math.Max(1, points.Count / 4000);
                for (var i = 0; i < points.Count; i += step)
                {
                    var p = points[i];
                    result.Add(new Vector2(0.5f + (p.X - (min.X + max.X) * 0.5f) / size.X * 0.82f,
                        0.96f - (p.Z - min.Z) / size.Z * 0.9f));
                }
                return result;
            }
            catch { return new(); }
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                if (_showImGuiMenu) { m.Result = (IntPtr)1; return; }
                m.Result = (IntPtr)HTTRANSPARENT; return;
            }
            base.WndProc(ref m);
        }
        protected override void OnPaint(PaintEventArgs e) { /* GPU swapchain owns the surface. */ }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        private void ApplyFullscreenBounds()
        {
            var screen = Screen.PrimaryScreen;
            if (screen is not null) Bounds = screen.Bounds;
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.Idle -= OnApplicationIdle;
            _timer.Stop(); _boundsTimer.Stop(); _menuKeyTimer.Stop(); _menu?.Close();
            _imguiRenderer?.Dispose(); _commandList?.Dispose(); _graphics?.Dispose();
            if (_imguiContext != IntPtr.Zero) { ImGui.SetCurrentContext(_imguiContext); ImGui.DestroyContext(_imguiContext); _imguiContext = IntPtr.Zero; }
            _settings.Save(); _tracker.Dispose();
            timeEndPeriod(1);
            base.OnFormClosed(e);
        }
    }

    /// <summary>
    /// Independent tactical radar window.  It consumes the same read-only
    /// EspTracker snapshot as the ESP overlay and the map metadata shipped by
    /// wardogs-calculator-main.  No target-process writes or input injection
    /// are performed here.
    /// </summary>
    // The radar has its own STA message loop. GDI map painting and tile
    // bitmap resampling therefore cannot block the ESP/D3D window's loop.
    private sealed class RadarForm : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private RadarWindow? _window;
        private Exception? _startupError;
        private int _disposed;

        public RadarForm(EspTracker tracker, EspSettings settings)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    using var window = new RadarWindow(tracker, settings);
                    _window = window;
                    _ready.Set();
                    Application.Run(window);
                }
                catch (Exception ex)
                {
                    _startupError = ex;
                    _ready.Set();
                }
                finally
                {
                    _window = null;
                    Volatile.Write(ref _disposed, 1);
                }
            }) { IsBackground = true, Name = "Wardogs radar UI", }; 
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
            if (_startupError is not null) throw new InvalidOperationException("Radar UI initialization failed.", _startupError);
        }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0 || _window is null;

        public void Show() => Post(window => { if (!window.Visible) window.Show(); window.BringToFront(); });
        public void BringToFront() => Post(window => { if (!window.Visible) window.Show(); window.BringToFront(); });
        public void Close()
        {
            var window = _window;
            if (window is null || window.IsDisposed) return;
            try { window.BeginInvoke((Action)(() => window.Close())); } catch (InvalidOperationException) { }
        }

        private void Post(Action<RadarWindow> action)
        {
            var window = _window;
            if (window is null || window.IsDisposed || !window.IsHandleCreated) return;
            try { window.BeginInvoke(action, window); } catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Close();
            if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(3000);
            _ready.Dispose();
        }
    }

    private sealed class RadarWindow : Form
    {
        private readonly EspTracker _tracker;
        private readonly EspSettings _settings;
        private readonly RadarCanvas _canvas;
        private readonly ComboBox _mapSelect;
        private readonly CheckBox _names;
        private readonly CheckBox _vehicles;
        private readonly CheckBox _teammates;
        private readonly CheckBox _enemies;
        private readonly CheckBox _weapons;
        private readonly CheckBox _rotate;
        private readonly TrackBar _zoom;
        private readonly Label _status;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly IReadOnlyList<RadarMapData> _maps;

        public RadarWindow(EspTracker tracker, EspSettings settings)
        {
            _tracker = tracker;
            _settings = settings;
            _maps = RadarMapData.LoadAll(settings.RadarMapDirectory);
            Text = "WARDOGS 2D Radar";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 760);
            MinimumSize = new Size(640, 500);
            BackColor = Color.FromArgb(12, 18, 21);
            ForeColor = Color.White;
            TopMost = true;

            var menu = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 64, Padding = new Padding(8, 8, 8, 4),
                BackColor = Color.FromArgb(24, 31, 35), WrapContents = false, AutoScroll = true
            };
            menu.Controls.Add(new Label { Text = "地图", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _mapSelect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(0, 0, 10, 0) };
            foreach (var map in _maps) _mapSelect.Items.Add(map.Name);
            var selected = Math.Max(0, _maps.ToList().FindIndex(x => x.Id.Equals(settings.RadarMapId, StringComparison.OrdinalIgnoreCase)));
            if (_mapSelect.Items.Count > 0) _mapSelect.SelectedIndex = selected;
            _mapSelect.SelectedIndexChanged += (_, _) => { if (_mapSelect.SelectedIndex >= 0) { _settings.RadarMapId = _maps[_mapSelect.SelectedIndex].Id; _canvas!.Map = _maps[_mapSelect.SelectedIndex]; _canvas!.ResetPan(); } };
            menu.Controls.Add(_mapSelect);
            _names = AddCheck(menu, "名称", settings.RadarShowNames, v => settings.RadarShowNames = v);
            _vehicles = AddCheck(menu, "载具", settings.RadarShowVehicles, v => settings.RadarShowVehicles = v);
            _teammates = AddCheck(menu, "队友", settings.RadarShowTeammates, v => settings.RadarShowTeammates = v);
            _enemies = AddCheck(menu, "敌人", settings.RadarShowEnemies, v => settings.RadarShowEnemies = v);
            _weapons = AddCheck(menu, "武器", settings.RadarShowWeapons, v => settings.RadarShowWeapons = v);
            _rotate = AddCheck(menu, "随本地朝向旋转", settings.RadarRotateWithLocal, v => settings.RadarRotateWithLocal = v);
            menu.Controls.Add(new Label { Text = "缩放(滚轮)", AutoSize = true, Margin = new Padding(10, 6, 4, 0) });
            _zoom = new TrackBar { Minimum = 25, Maximum = 3000, Value = (int)Math.Clamp(settings.RadarZoom * 100, 25, 3000), TickFrequency = 250, Width = 150, Height = 32, Margin = new Padding(0, -2, 8, 0) };
            _zoom.Scroll += (_, _) => { settings.RadarZoom = _zoom.Value / 100f; _canvas!.Invalidate(); };
            menu.Controls.Add(_zoom);
            var save = new Button { Text = "保存", AutoSize = true, Margin = new Padding(0, 0, 4, 0) };
            save.Click += (_, _) => settings.Save();
            menu.Controls.Add(save);
            var close = new Button { Text = "关闭", AutoSize = true };
            // Hide instead of disposing so the ESP menu can reopen the same
            // independent window without starting a second reader.
            close.Click += (_, _) => Hide();
            menu.Controls.Add(close);
            var markers = new Button { Text = "标记面板", AutoSize = true, Margin = new Padding(8, 0, 4, 0) };
            markers.Click += (_, _) => _canvas!.ShowMarkerPanel();
            menu.Controls.Add(markers);
            Controls.Add(menu);

            _canvas = new RadarCanvas(tracker, settings, _maps.FirstOrDefault(x => x.Id.Equals(settings.RadarMapId, StringComparison.OrdinalIgnoreCase)) ?? _maps.FirstOrDefault());
            _canvas.Dock = DockStyle.Fill;
            Controls.Add(_canvas);
            _status = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 3, 0, 0), BackColor = Color.FromArgb(24, 31, 35), ForeColor = Color.Gainsboro };
            Controls.Add(_status);
            // The radar is a UI consumer of the tracker snapshot.  It does
            // not perform another DMA scan here, so a 16 ms cadence keeps
            // camera/heading motion smooth while the reader thread controls
            // the actual memory-read rate independently.
            // Radar painting uses GDI and shares the WinForms UI thread with
            // the ESP host. A 10 Hz UI cadence prevents a large map bitmap
            // from starving the uncapped ESP renderer (which previously
            // dropped to 1 FPS while this window was open).
            // Radar contacts are supplied by the reader snapshot; painting
            // the full GDI map need not run at 60 Hz and should not compete
            // with the ESP swapchain on the shared UI thread.
            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += (_, _) =>
            {
                _canvas.Invalidate();
                var zoomValue = (int)Math.Clamp(settings.RadarZoom * 100, 25, 3000);
                if (_zoom.Value != zoomValue) _zoom.Value = zoomValue;
                _status.Text = $"玩家 {_canvas.PlayerCount}  |  载具 {_canvas.VehicleCount}  |  范围 {_canvas.InBoundsCount}/{_canvas.ContactCount}  |  坐标 {_canvas.LocalCoordinate}  |  {_canvas.OriginStatus}  |  雷达 {_canvas.RenderFps:0} FPS  |  左键/中键拖动，滚轮缩放  |  Insert：ESP菜单";
            };
            _timer.Start();
            FormClosed += (_, _) => _timer.Stop();
        }

        private static CheckBox AddCheck(Control parent, string text, bool value, Action<bool> changed)
        {
            var box = new CheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = Color.White, Margin = new Padding(4, 5, 4, 0) };
            box.CheckedChanged += (_, _) => changed(box.Checked);
            parent.Controls.Add(box);
            return box;
        }
    }

    private sealed class RadarMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
        public double X { get; }
        public double Y { get; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public RadarMarker(double x, double y) { X = x; Y = y; }
    }

    private sealed class MarkerPanelForm : Form
    {
        private readonly RadarCanvas _canvas;
        private readonly ListView _list;
        private readonly System.Windows.Forms.Timer _timer;
        private Point _dragStart; private bool _dragging;
        public MarkerPanelForm(RadarCanvas canvas)
        {
            _canvas = canvas; Text = "迫击炮标点"; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; TopMost = true; ClientSize = new Size(760, 280);
            BackColor = Color.FromArgb(24, 30, 34); ForeColor = Color.White;
            var header = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.FromArgb(38, 50, 56), Cursor = Cursors.SizeAll };
            var title = new Label { Text = "迫击炮标点 / 双击左键添加，双击右键删除", AutoSize = true, Location = new Point(10, 9), ForeColor = Color.White };
            var close = new Button { Text = "×", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.Transparent, Width = 30, Height = 28, Dock = DockStyle.Right };
            close.FlatAppearance.BorderSize = 0; close.Click += (_, _) => Hide();
            header.Controls.Add(title); header.Controls.Add(close); Controls.Add(header);
            header.MouseDown += BeginDrag; header.MouseMove += Drag; header.MouseUp += EndDrag; title.MouseDown += BeginDrag; title.MouseMove += Drag; title.MouseUp += EndDrag;
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, BackColor = Color.FromArgb(18, 24, 27), ForeColor = Color.White };
            _list.Columns.Add("标记", 52); _list.Columns.Add("坐标", 142); _list.Columns.Add("距离(m)", 78);
            _list.Columns.Add("方位(°)", 78); _list.Columns.Add("L81 低/高", 142); _list.Columns.Add("SPH-2 低/高", 154);
            // Add the fill control first, then keep the draggable title bar in
            // front. Adding a Dock=Fill ListView last used to paint over the
            // header and hide the first row's column titles.
            Controls.Add(_list); header.BringToFront();
            _timer = new System.Windows.Forms.Timer { Interval = 250 }; _timer.Tick += (_, _) => RefreshRows(); _timer.Start(); RefreshRows(); FormClosed += (_, _) => _timer.Stop();
        }
        private void BeginDrag(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; } }
        private void Drag(object? s, MouseEventArgs e) { if (_dragging) { var p = PointToScreen(e.Location); Location = new Point(p.X - _dragStart.X, p.Y - _dragStart.Y); } }
        private void EndDrag(object? s, MouseEventArgs e) { _dragging = false; }
        private void RefreshRows()
        {
            var rows = _canvas.GetMarkerRows();
            _list.BeginUpdate(); _list.Items.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i]; var item = new ListViewItem((i + 1).ToString()); item.SubItems.Add(r.Coordinate); item.SubItems.Add(r.Distance); item.SubItems.Add(r.Bearing); item.SubItems.Add(r.L81); item.SubItems.Add(r.Sph2); _list.Items.Add(item);
            }
            _list.EndUpdate();
        }
    }

    private sealed record MortarSolution(string Name, string ShortName, double? LowMil, double? HighMil, double MinRangeKm, double MaxRangeKm);

    private static class MortarCalculator
    {
        private sealed class WeaponDef
        {
            public string Id = string.Empty; public string Name = string.Empty;
            public double MinKm; public double MaxKm;
            public List<(double Distance, double Mil)> Low = new();
            public List<(double Distance, double Mil)> High = new();
            public List<(double Distance, double Mil)> Single = new();
        }
        private static readonly Lazy<IReadOnlyList<WeaponDef>> _weapons = new(Load);
        public static IReadOnlyList<MortarSolution> SolveAll(double distanceMeters)
        {
            var result = new List<MortarSolution>();
            foreach (var w in _weapons.Value)
            {
                var inRange = distanceMeters >= w.MinKm * 1000.0 && distanceMeters <= w.MaxKm * 1000.0;
                var low = inRange ? Interpolate(w.Low.Count > 0 ? w.Low : w.Single, distanceMeters) : null;
                var high = inRange ? Interpolate(w.High.Count > 0 ? w.High : w.Single, distanceMeters) : null;
                result.Add(new MortarSolution(w.Name, w.Id, low, high, w.MinKm, w.MaxKm));
            }
            return result;
        }
        private static double? Interpolate(List<(double Distance, double Mil)> table, double meters)
        {
            if (table.Count == 0) return null;
            table.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (meters < table[0].Distance || meters > table[^1].Distance) return null;
            for (var i = 0; i < table.Count; i++)
            {
                if (Math.Abs(table[i].Distance - meters) < 1e-6) return table[i].Mil;
                if (i + 1 >= table.Count || meters > table[i + 1].Distance) continue;
                var a = table[i]; var b = table[i + 1];
                var t = (meters - a.Distance) / Math.Max(1e-9, b.Distance - a.Distance);
                return a.Mil + (b.Mil - a.Mil) * t;
            }
            return null;
        }
        private static IReadOnlyList<WeaponDef> Load()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "wardogs-calculator-main", "data", "weapons.json"),
                @"I:\wardogs\wardogs-calculator-main\data\weapons.json"
            };
            foreach (var path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    var source = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("weapons");
                    var result = new List<WeaponDef>();
                    foreach (var item in source.EnumerateArray())
                    {
                        var w = new WeaponDef
                        {
                            Id = item.GetProperty("id").GetString() ?? "weapon",
                            Name = item.TryGetProperty("names", out var names) && (names.TryGetProperty("zh-cn", out var zh) || names.TryGetProperty("en", out zh)) ? zh.GetString() ?? "" : item.GetProperty("id").GetString() ?? "weapon",
                            MinKm = item.TryGetProperty("minRangeKm", out var min) ? min.GetDouble() : 0,
                            MaxKm = item.TryGetProperty("maxRangeKm", out var max) ? max.GetDouble() : item.GetProperty("rangeKm").GetDouble()
                        };
                        if (!item.TryGetProperty("ballistics", out var ballistic)) continue;
                        ReadTable(ballistic, "single", w.Single); ReadTable(ballistic, "low", w.Low); ReadTable(ballistic, "high", w.High);
                        if (w.Single.Count > 0 || w.Low.Count > 0 || w.High.Count > 0) result.Add(w);
                    }
                    return result;
                }
                catch { }
            }
            return Array.Empty<WeaponDef>();
        }
        private static void ReadTable(JsonElement ballistic, string name, List<(double Distance, double Mil)> output)
        {
            if (!ballistic.TryGetProperty(name, out var table) || table.ValueKind != JsonValueKind.Array) return;
            foreach (var row in table.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 2) output.Add((row[0].GetDouble(), row[1].GetDouble()));
        }
    }

    private sealed class RadarCanvas : Panel
    {
        private readonly EspTracker _tracker;
        private readonly EspSettings _settings;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
        private readonly object _mapImageSync = new();
        private Bitmap? _mapImage;
        private Bitmap? _scaledMapImage;
        private string? _scaledMapImageKey;
        private Bitmap? _staticLayer;
        private string? _staticLayerKey;
        private string? _mapImageKey;
        private string? _mapImageLoading;
        private string? _mapImageError;
        private float _panX;
        private float _panY;
        private Point _panStart;
        private float _panStartX;
        private float _panStartY;
        private bool _panning;
        private readonly object _markerSync = new();
        private readonly List<RadarMarker> _markers = new();
        private MarkerPanelForm? _markerPanel;
        private readonly Stopwatch _paintClock = Stopwatch.StartNew();
        private int _paintFrames;
        private double _renderFps;
        public RadarMapData? Map { get; set; }
        public int PlayerCount { get; private set; }
        public int VehicleCount { get; private set; }
        public int InBoundsCount { get; private set; }
        public int ContactCount { get; private set; }
        public string LocalCoordinate { get; private set; } = "--";
        public string OriginStatus { get; private set; } = "origin --";
        public double RenderFps => _renderFps;

        public RadarCanvas(EspTracker tracker, EspSettings settings, RadarMapData? map)
        {
            _tracker = tracker; _settings = settings; Map = map;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true; TabStop = true; BackColor = Color.FromArgb(10, 15, 17);
        }

        public void ShowMarkerPanel()
        {
            if (_markerPanel is null || _markerPanel.IsDisposed) _markerPanel = new MarkerPanelForm(this);
            if (!_markerPanel.Visible) _markerPanel.Show(this.FindForm());
            _markerPanel.BringToFront();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (Map is not null)
            {
                if (e.Button == MouseButtons.Left && TryScreenToMap(e.Location, out var x, out var y))
                {
                    lock (_markerSync) _markers.Add(new RadarMarker(x, y));
                    Invalidate();
                }
                else if (e.Button == MouseButtons.Right)
                {
                    lock (_markerSync)
                    {
                        var hit = _markers.Select(m => (Marker: m, Distance: MarkerPixelDistance(m, e.Location))).Where(x => x.Distance <= 18).OrderBy(x => x.Distance).FirstOrDefault();
                        if (hit.Marker is not null) _markers.Remove(hit.Marker);
                    }
                    Invalidate();
                }
            }
            _panning = false; Capture = false;
            base.OnMouseDoubleClick(e);
        }

        private double MarkerPixelDistance(RadarMarker marker, Point location)
        {
            if (!TryGetRender(out var origin, out var scale, out var worldW, out var worldH)) return double.MaxValue;
            var p = MapToPoint(marker.X, marker.Y, origin, scale);
            ApplyMarkerRotation(ref p, origin, worldW, worldH, inverse: false);
            return Math.Sqrt(Math.Pow(p.X - location.X, 2) + Math.Pow(p.Y - location.Y, 2));
        }

        private bool TryScreenToMap(Point point, out double x, out double y)
        {
            x = y = 0;
            if (!TryGetRender(out var origin, out var scale, out var worldW, out var worldH) || scale <= 0) return false;
            var screen = new PointF(point.X, point.Y);
            ApplyMarkerRotation(ref screen, origin, worldW, worldH, inverse: true);
            x = Map!.RenderMinX + (screen.X - origin.X) / scale;
            y = Map.RenderMaxY - (screen.Y - origin.Y) / scale;
            return x >= Map.RenderMinX && x <= Map.RenderMaxX && y >= Map.RenderMinY && y <= Map.RenderMaxY;
        }

        private void ApplyMarkerRotation(ref PointF point, PointF origin, float worldW, float worldH, bool inverse)
        {
            if (!_settings.RadarRotateWithLocal || Map is null) return;
            var local = _tracker.RadarSnapshot.FirstOrDefault(x => x.IsLocal);
            if (!local.IsLocal) return;
            var localMap = WorldToMap(local.WorldPosition, Map, 0, 0);
            var scale = Math.Min((ClientSize.Width - 48f) / Math.Max(1f, worldW), (ClientSize.Height - 48f) / Math.Max(1f, worldH)) * Math.Max(1f, _settings.RadarZoom);
            var localPoint = MapToPoint(localMap.X, localMap.Y, origin, scale);
            var center = new PointF(origin.X + worldW * scale / 2f, origin.Y + worldH * scale / 2f);
            var yaw = _tracker.CameraRotation.Y; if (!float.IsFinite(yaw)) yaw = local.Yaw;
            var deg = -90f - ScreenHeadingYaw(yaw, Map); if (inverse) deg = -deg;
            var rad = deg * Math.PI / 180.0; var dx = point.X - (inverse ? center.X : localPoint.X); var dy = point.Y - (inverse ? center.Y : localPoint.Y);
            point = new PointF((inverse ? localPoint.X : center.X) + (float)(dx * Math.Cos(rad) - dy * Math.Sin(rad)), (inverse ? localPoint.Y : center.Y) + (float)(dx * Math.Sin(rad) + dy * Math.Cos(rad)));
        }

        private bool TryGetRender(out PointF origin, out float scale, out float worldW, out float worldH)
        {
            origin = default; scale = 0; worldW = worldH = 0;
            if (Map is null) return false;
            var margin = 24f; var rect = new RectangleF(margin, margin, Math.Max(1, ClientSize.Width - margin * 2), Math.Max(1, ClientSize.Height - margin * 2));
            worldW = (float)(Map.RenderMaxX - Map.RenderMinX); worldH = (float)(Map.RenderMaxY - Map.RenderMinY);
            scale = Math.Min(rect.Width / Math.Max(1, worldW), rect.Height / Math.Max(1, worldH)) * Math.Max(1f, _settings.RadarZoom);
            var drawW = worldW * scale; var drawH = worldH * scale;
            origin = new PointF(rect.X + (rect.Width - drawW) / 2 + _panX, rect.Y + (rect.Height - drawH) / 2 + _panY);
            return true;
        }

        private PointF MapToPoint(double x, double y, PointF origin, float scale) => new(origin.X + (float)(x - Map!.RenderMinX) * scale, origin.Y + (float)(Map.RenderMaxY - y) * scale);

        public IReadOnlyList<(string Coordinate, string Distance, string Bearing, string L81, string Sph2)> GetMarkerRows()
        {
            var map = Map; if (map is null) return Array.Empty<(string, string, string, string, string)>();
            var snapshot = _tracker.RadarSnapshot; var local = snapshot.FirstOrDefault(x => x.IsLocal);
            if (!local.IsLocal)
                local = snapshot.FirstOrDefault(x => x.Actor == _tracker.ActiveLocalActor);
            if (!local.IsLocal) return Array.Empty<(string, string, string, string, string)>();
            var originX = ResolveWorldOrigin(local.WorldPosition.X, map.MinX, map.MaxX, map.WorldOriginX);
            var originY = ResolveWorldOrigin(local.WorldPosition.Y, map.MinY, map.MaxY, map.WorldOriginY);
            var (lx, ly) = WorldToMap(local.WorldPosition, map, originX, originY);
            lock (_markerSync)
            {
                return _markers.Select((m, i) =>
                {
                    var dx = m.X - lx; var dy = m.Y - ly; var meters = Math.Sqrt(dx * dx + dy * dy) * map.CoordinateMetersPerUnit;
                    var azimuth = Math.Atan2(dx, dy) * 180 / Math.PI; if (azimuth < 0) azimuth += 360;
                    var solutions = MortarCalculator.SolveAll(meters);
                    static string Format(IReadOnlyList<MortarSolution> all, string id)
                    {
                        var s = all.FirstOrDefault(x => x.ShortName.Equals(id, StringComparison.OrdinalIgnoreCase));
                        return s is null ? "-- / --" : $"{(s.LowMil.HasValue ? $"{s.LowMil:0}" : "--")} / {(s.HighMil.HasValue ? $"{s.HighMil:0}" : "--")}";
                    }
                    return ($"{m.X:0.00},{m.Y:0.00}", $"{meters:0}", $"{azimuth:0.0}", Format(solutions, "mortar"), Format(solutions, "spg"));
                }).ToArray();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // A WinForms panel receives the wheel only while it owns focus.
            // Focus it when the pointer enters so the radar can be zoomed
            // immediately without an extra click.
            Focus();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            {
                Focus(); Capture = true; _panning = true; _panStart = e.Location;
                _panStartX = _panX; _panStartY = _panY;
                Cursor = Cursors.SizeAll;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_panning)
            {
                _panX = _panStartX + e.X - _panStart.X;
                _panY = _panStartY + e.Y - _panStart.Y;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            {
                _panning = false; Capture = false; Cursor = Cursors.Default;
            }
            base.OnMouseUp(e);
        }

        public void ResetPan() { _panX = 0; _panY = 0; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
            var oldZoom = Math.Clamp(_settings.RadarZoom, 0.25f, 30f);
            var newZoom = Math.Clamp(oldZoom * factor, 0.25f, 30f);
            if (Map is not null && Math.Abs(newZoom - oldZoom) > 0.0001f)
            {
                // Keep the map point under the cursor fixed while zooming.
                var margin = 24f;
                var rect = new RectangleF(margin, margin, Math.Max(1, ClientSize.Width - margin * 2), Math.Max(1, ClientSize.Height - margin * 2));
                var worldW = (float)(Map.RenderMaxX - Map.RenderMinX); var worldH = (float)(Map.RenderMaxY - Map.RenderMinY);
                var oldScale = Math.Min(rect.Width / Math.Max(1, worldW), rect.Height / Math.Max(1, worldH)) * Math.Max(1f, oldZoom);
                var newScale = Math.Min(rect.Width / Math.Max(1, worldW), rect.Height / Math.Max(1, worldH)) * Math.Max(1f, newZoom);
                var centeredOld = new PointF(rect.X + (rect.Width - worldW * oldScale) / 2 + _panX,
                    rect.Y + (rect.Height - worldH * oldScale) / 2 + _panY);
                var centered = new PointF(rect.X + (rect.Width - worldW * newScale) / 2,
                    rect.Y + (rect.Height - worldH * newScale) / 2);
                _panX = e.Location.X - (e.Location.X - centeredOld.X) * newScale / Math.Max(0.001f, oldScale) - centered.X;
                _panY = e.Location.Y - (e.Location.Y - centeredOld.Y) * newScale / Math.Max(0.001f, oldScale) - centered.Y;
            }
            _settings.RadarZoom = newZoom;
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _paintFrames++;
            var elapsed = _paintClock.Elapsed.TotalSeconds;
            if (elapsed >= 0.5)
            {
                _renderFps = _paintFrames / elapsed;
                _paintFrames = 0;
                _paintClock.Restart();
            }
            // Static terrain/grid is cached below; use the fast raster path
            // for the dynamic contact pass so a crowded radar stays smooth.
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
            var g = e.Graphics;
            g.Clear(BackColor);
            var map = Map;
            if (map is null || map.MaxX <= map.MinX || map.MaxY <= map.MinY)
            {
                using var empty = new SolidBrush(Color.Gainsboro);
                g.DrawString("未找到地图数据：请设置 RadarMapDirectory", Font, empty, 24, 24);
                return;
            }
            var margin = 24f;
            var rect = new RectangleF(margin, margin, Math.Max(1, ClientSize.Width - margin * 2), Math.Max(1, ClientSize.Height - margin * 2));
            var tileZoom = SelectTileZoom(map, Math.Max(rect.Width, rect.Height));
            var tileImageKey = $"{map.Id}:z{tileZoom}";
            EnsureMapImage(map, rect.Width, rect.Height);
            var worldW = (float)(map.RenderMaxX - map.RenderMinX); var worldH = (float)(map.RenderMaxY - map.RenderMinY);
            // Values below 1.0 used to shrink the complete map into a small
            // square (as in the reported screenshot).  The radar is always
            // fitted to the client area; the slider now only zooms in from
            // that fitted size.
            var scale = Math.Min(rect.Width / worldW, rect.Height / worldH) * Math.Max(1f, _settings.RadarZoom);
            var drawW = worldW * scale; var drawH = worldH * scale;
            var origin = new PointF(rect.X + (rect.Width - drawW) / 2 + _panX, rect.Y + (rect.Height - drawH) / 2 + _panY);
            var camera = _tracker.CameraLocation;
            var worldOriginX = ResolveWorldOrigin(camera.X, map.MinX, map.MaxX, map.WorldOriginX);
            var worldOriginY = ResolveWorldOrigin(camera.Y, map.MinY, map.MaxY, map.WorldOriginY);
            DrawStaticLayer(g, map, tileImageKey, origin, drawW, drawH, worldW, worldH, scale);

            var snapshot = _tracker.RadarSnapshot;
            ContactCount = snapshot.Count;
            InBoundsCount = snapshot.Count(item =>
            {
                var (mx, my) = WorldToMap(item.WorldPosition, map, worldOriginX, worldOriginY);
                return mx >= map.MinX && mx <= map.MaxX && my >= map.MinY && my <= map.MaxY;
            });
            OriginStatus = map.HasWorldTransform
                ? $"transform {map.Id} calibrated"
                : $"origin {worldOriginX:0}/{worldOriginY:0}";
            var local = snapshot.FirstOrDefault(x => x.IsLocal);
            if (!local.IsPlayer)
                local = snapshot.FirstOrDefault(x => x.Actor == _tracker.ActiveLocalActor);
            var hasLocal = local.IsLocal;
            var cameraYaw = _tracker.CameraRotation.Y;
            if (!float.IsFinite(cameraYaw) || MathF.Abs(cameraYaw) > 360f) cameraYaw = hasLocal ? local.Yaw : 0;
            // Contacts are rotated around the local player when the radar is
            // in heading-up mode.  Keep this exact screen-space rotation for
            // both positions and heading lines; otherwise the points move
            // while their arrows remain in map space (which mirrors two
            // opposite relative directions).
            var radarRotationDeg = hasLocal && _settings.RadarRotateWithLocal
                ? -90f - ScreenHeadingYaw(cameraYaw, map)
                : 0f;
            DrawRadarMarkers(g, map, origin, scale, worldOriginX, worldOriginY, local, hasLocal, radarRotationDeg);
            PlayerCount = 0; VehicleCount = 0;
            LocalCoordinate = "--";
            var clipState = g.Save();
            g.SetClip(new RectangleF(origin.X, origin.Y, drawW, drawH));
            foreach (var item in snapshot)
            {
                if (item.IsPlayer) PlayerCount++; else VehicleCount++;
                if (!item.IsPlayer && !_settings.RadarShowVehicles) continue;
                // The local contact is a mandatory orientation anchor and
                // must remain visible even when the user hides teammates or
                // enemies on the radar.
                if (!item.IsLocal && item.IsPlayer && item.Friendly && !_settings.RadarShowTeammates) continue;
                if (!item.IsLocal && item.IsPlayer && !item.Friendly && item.TeamKnown && !_settings.RadarShowEnemies) continue;
                var p = WorldToPoint(item.WorldPosition, map, origin, scale, worldOriginX, worldOriginY);
                if (hasLocal && _settings.RadarRotateWithLocal)
                {
                    var localPoint = WorldToPoint(local.WorldPosition, map, origin, scale, worldOriginX, worldOriginY);
                    var radarCenter = new PointF(origin.X + drawW / 2, origin.Y + drawH / 2);
                    // Convert UE yaw to the screen-space heading first.  On
                    // Bakurani/Ozeti WorldAxisSignY=-1, so increasing UE Y
                    // goes down the bitmap and the screen angle is +yaw.
                    var radians = radarRotationDeg * Math.PI / 180.0;
                    var dx = p.X - localPoint.X; var dy = p.Y - localPoint.Y;
                    // Keep the local contact at the radar centre while the
                    // world rotates so its heading always points up-map.
                    p = new PointF(radarCenter.X + (float)(dx * Math.Cos(radians) - dy * Math.Sin(radians)),
                        radarCenter.Y + (float)(dx * Math.Sin(radians) + dy * Math.Cos(radians)));
                }
                if (item.IsLocal)
                {
                    var mapRect = new RectangleF(origin.X, origin.Y, drawW, drawH);
                    // If a transient origin/transform sample places the local
                    // pawn just outside the loaded terrain, keep the anchor
                    // on the nearest map edge instead of clipping it away.
                    p = ClampToRect(p, mapRect);
                }
                var color = item.IsLocal ? Color.White : item.Downed ? Color.Orange : item.TeamKnown ? (item.Friendly ? Color.LimeGreen : Color.Red) : Color.Gold;
                var headingYaw = item.IsLocal && item.IsPlayer ? cameraYaw : item.Yaw;
                DrawContact(g, p, headingYaw, color, item.IsLocal ? 7 : item.IsPlayer ? 5 : 6,
                    hasLocal && _settings.RadarRotateWithLocal ? cameraYaw : 0,
                    item.IsLocal && hasLocal && _settings.RadarRotateWithLocal, radarRotationDeg, map);
                if (_settings.RadarShowNames || (_settings.RadarShowWeapons && !string.IsNullOrWhiteSpace(item.Weapon)))
                {
                    var label = _settings.RadarShowNames ? (item.IsLocal ? "LOCAL" : item.Name) : string.Empty;
                    if (_settings.RadarShowWeapons && !string.IsNullOrWhiteSpace(item.Weapon)) label += $"  [{item.Weapon}]";
                    using var brush = new SolidBrush(color);
                    g.DrawString(label, Font, brush, p.X + 8, p.Y - 8);
                }
            }
            g.Restore(clipState);
            if (hasLocal)
            {
                var (localMapX, localMapY) = WorldToMap(local.WorldPosition, map, worldOriginX, worldOriginY);
                LocalCoordinate = $"{localMapX:0.0}, {localMapY:0.0}";
            }
            using var fpsBack = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            using var fpsBrush = new SolidBrush(Color.Lime);
            var fpsText = $"Radar FPS: {_renderFps:0.0} | Contacts: {ContactCount}";
            g.FillRectangle(fpsBack, rect.Left + 8, rect.Top + 8, 178, 22);
            g.DrawString(fpsText, Font, fpsBrush, rect.Left + 12, rect.Top + 11);
        }

        private void DrawStaticLayer(Graphics target, RadarMapData map, string tileImageKey,
            PointF origin, float drawW, float drawH, float worldW, float worldH, float scale)
        {
            string imageKey;
            string loading;
            string error;
            lock (_mapImageSync)
            {
                imageKey = _mapImageKey ?? string.Empty;
                loading = _mapImageLoading ?? string.Empty;
                error = _mapImageError ?? string.Empty;
            }
            var key = $"{map.Id}|{tileImageKey}|{ClientSize.Width}x{ClientSize.Height}|{origin.X:0.0},{origin.Y:0.0},{drawW:0.0},{drawH:0.0}|{imageKey}|{loading}|{error}";
            if (_staticLayer is not null && string.Equals(_staticLayerKey, key, StringComparison.Ordinal))
            {
                target.DrawImageUnscaled(_staticLayer, 0, 0);
                return;
            }

            var layer = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height),
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(layer))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
                g.Clear(BackColor);
                using (var bg = new SolidBrush(Color.FromArgb(20, 40, 36)))
                    g.FillRectangle(bg, origin.X, origin.Y, drawW, drawH);
                lock (_mapImageSync)
                {
                    if (_mapImage is not null && string.Equals(_mapImageKey, tileImageKey, StringComparison.OrdinalIgnoreCase))
                    {
                        // Draw only the portion currently visible in the
                        // client. The old path allocated a bitmap as large as
                        // the *entire zoomed map* on every wheel event (up to
                        // tens of thousands of pixels), which caused the
                        // visible zoom hitch and large GC spikes.
                        var visible = RectangleF.Intersect(
                            new RectangleF(0, 0, ClientSize.Width, ClientSize.Height),
                            new RectangleF(origin.X, origin.Y, drawW, drawH));
                        if (visible.Width > 0 && visible.Height > 0)
                        {
                            var sx = (visible.Left - origin.X) / Math.Max(1f, drawW) * _mapImage.Width;
                            var sy = (visible.Top - origin.Y) / Math.Max(1f, drawH) * _mapImage.Height;
                            var sw = visible.Width / Math.Max(1f, drawW) * _mapImage.Width;
                            var sh = visible.Height / Math.Max(1f, drawH) * _mapImage.Height;
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                            using var attributes = new ImageAttributes();
                            var matrix = new ColorMatrix { Matrix33 = 0.68f, Matrix00 = 0.82f, Matrix11 = 0.82f, Matrix22 = 0.82f };
                            attributes.SetColorMatrix(matrix);
                            g.DrawImage(_mapImage, System.Drawing.Rectangle.Round(visible), sx, sy, sw, sh, GraphicsUnit.Pixel, attributes);
                        }
                    }
                    else
                    {
                        var message = _mapImageLoading is not null ? "正在加载地图底图…" :
                            _mapImageError is not null ? $"底图加载失败（{_mapImageError}），请检查网络" : "地图底图准备中…";
                        using var brush = new SolidBrush(Color.FromArgb(190, 225, 235, 220));
                        g.DrawString(message, Font, brush, origin.X + 12, origin.Y + 12);
                    }
                }
                using (var border = new Pen(Color.FromArgb(80, 150, 125), 2))
                    g.DrawRectangle(border, origin.X, origin.Y, drawW, drawH);
                DrawGrid(g, origin, drawW, drawH, worldW, worldH, scale);
                DrawMapDecorations(g, map, origin, scale);
                using var north = new Pen(Color.White, 2);
                g.DrawLine(north, ClientSize.Width - 26, 62, ClientSize.Width - 26, 36);
                g.DrawLine(north, ClientSize.Width - 26, 36, ClientSize.Width - 31, 45);
                g.DrawLine(north, ClientSize.Width - 26, 36, ClientSize.Width - 21, 45);
                g.DrawString("N", Font, Brushes.White, ClientSize.Width - 34, 64);
            }
            _staticLayer?.Dispose();
            _staticLayer = layer;
            _staticLayerKey = key;
            target.DrawImageUnscaled(layer, 0, 0);
        }

        private static PointF WorldToPoint(Vector3 world, RadarMapData map, PointF origin, float scale, double worldOriginX, double worldOriginY)
        {
            var (mapX, mapY) = WorldToMap(world, map, worldOriginX, worldOriginY);
            var x = (float)(mapX - map.RenderMinX) * scale + origin.X;
            var y = (float)(map.RenderMaxY - mapY) * scale + origin.Y;
            return new PointF(x, y);
        }

        private void DrawRadarMarkers(Graphics g, RadarMapData map, PointF origin, float scale, double worldOriginX, double worldOriginY, RadarItem local, bool hasLocal, float rotationDeg)
        {
            List<RadarMarker> markers;
            lock (_markerSync) markers = _markers.ToList();
            if (markers.Count == 0) return;
            PointF center = new(origin.X + (float)(map.RenderMaxX - map.RenderMinX) * scale / 2f, origin.Y + (float)(map.RenderMaxY - map.RenderMinY) * scale / 2f);
            var mapRect = new RectangleF(origin.X, origin.Y,
                (float)(map.RenderMaxX - map.RenderMinX) * scale,
                (float)(map.RenderMaxY - map.RenderMinY) * scale);
            PointF localPoint = hasLocal ? ClampToRect(WorldToPoint(local.WorldPosition, map, origin, scale, worldOriginX, worldOriginY), mapRect) : default;
            var radians = rotationDeg * Math.PI / 180.0;
            using var line = new Pen(Color.FromArgb(210, 255, 215, 80), 1.4f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var fill = new SolidBrush(Color.FromArgb(235, 255, 195, 30));
            using var outline = new Pen(Color.Black, 1.5f);
            using var text = new SolidBrush(Color.FromArgb(245, 255, 235, 150));
            for (var i = 0; i < markers.Count; i++)
            {
                var p = MapToPoint(markers[i].X, markers[i].Y, origin, scale);
                if (hasLocal && _settings.RadarRotateWithLocal)
                {
                    var dx = p.X - localPoint.X; var dy = p.Y - localPoint.Y;
                    p = new PointF(center.X + (float)(dx * Math.Cos(radians) - dy * Math.Sin(radians)), center.Y + (float)(dx * Math.Sin(radians) + dy * Math.Cos(radians)));
                }
                if (hasLocal) g.DrawLine(line, hasLocal && _settings.RadarRotateWithLocal ? center : localPoint, p);
                g.FillEllipse(fill, p.X - 6, p.Y - 6, 12, 12); g.DrawEllipse(outline, p.X - 6, p.Y - 6, 12, 12);
                g.DrawString($"M{i + 1}", Font, text, p.X + 8, p.Y - 8);
            }
        }

        private static PointF ClampToRect(PointF point, RectangleF rect)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                return new PointF(rect.Left + rect.Width / 2f, rect.Top + rect.Height / 2f);
            return new PointF(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom));
        }

        private static (double X, double Y) WorldToMap(Vector3 world, RadarMapData map, double worldOriginX, double worldOriginY)
        {
            if (map.HasWorldTransform)
            {
                // UE FVector values are centimetres in this build.  The
                // calculator's coordinateMetersPerUnit is metres per map
                // coordinate, and the Y axis follows the web map's north-up
                // convention (Bakurani/Ozeti therefore use a negative sign).
                var worldMetersX = world.X / 100.0;
                var worldMetersY = world.Y / 100.0;
                return ((worldMetersX - map.WorldOffsetMetersX) / map.CoordinateMetersPerUnit * map.WorldAxisSignX,
                    (worldMetersY - map.WorldOffsetMetersY) / map.CoordinateMetersPerUnit * map.WorldAxisSignY);
            }
            // Legacy/custom maps without a documented transform retain the
            // configurable absolute-origin path.
            return ((world.X - worldOriginX) / map.CoordinateMetersPerUnit,
                (world.Y - worldOriginY) / map.CoordinateMetersPerUnit);
        }

        private void EnsureMapImage(RadarMapData map, float viewWidth, float viewHeight)
        {
            if (string.IsNullOrWhiteSpace(map.TilePath)) return;
            var zoom = SelectTileZoom(map, Math.Max(viewWidth, viewHeight));
            var imageKey = $"{map.Id}:z{zoom}";
            lock (_mapImageSync)
            {
                if (string.Equals(_mapImageKey, imageKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_mapImageLoading, imageKey, StringComparison.OrdinalIgnoreCase)) return;
                _mapImageLoading = imageKey;
                _mapImageError = null;
            }
            _ = Task.Run(async () =>
            {
                Bitmap? mosaic = null;
                try
                {
                    var tilesPerAxis = 1 << zoom;
                    var tileSize = Math.Max(64, map.TileSize);
                    mosaic = new Bitmap(tilesPerAxis * tileSize, tilesPerAxis * tileSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var canvas = Graphics.FromImage(mosaic))
                    {
                        canvas.Clear(Color.FromArgb(35, 45, 42));
                        var jobs = new List<Task<(int X, int Y, byte[]? Bytes)>>();
                        for (var ty = 0; ty < tilesPerAxis; ty++)
                            for (var tx = 0; tx < tilesPerAxis; tx++)
                            {
                                var x = tx; var y = ty;
                                jobs.Add(LoadTileAsync(map, zoom, x, y));
                            }
                        var loaded = await Task.WhenAll(jobs).ConfigureAwait(false);
                        var loadedCount = loaded.Count(tile => tile.Bytes is not null);
                        if (loadedCount == 0) throw new InvalidOperationException("official map tiles unavailable");
                        foreach (var tile in loaded)
                        {
                            if (tile.Bytes is null) continue;
                            using var decoded = SixLabors.ImageSharp.Image.Load<Rgba32>(tile.Bytes);
                            using var tileBitmap = new Bitmap(decoded.Width, decoded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            for (var y = 0; y < decoded.Height; y++)
                                for (var x = 0; x < decoded.Width; x++)
                                {
                                    var px = decoded[x, y];
                                    tileBitmap.SetPixel(x, y, Color.FromArgb(px.A, px.R, px.G, px.B));
                                }
                            canvas.DrawImage(tileBitmap, tile.X * tileSize, tile.Y * tileSize, tileSize, tileSize);
                        }
                    }
                    lock (_mapImageSync)
                    {
                        if (!string.Equals(_mapImageLoading, imageKey, StringComparison.OrdinalIgnoreCase))
                        {
                            mosaic.Dispose();
                            return;
                        }
                        _mapImage?.Dispose(); _scaledMapImage?.Dispose(); _scaledMapImage = null; _scaledMapImageKey = null;
                        _mapImage = mosaic; _mapImageKey = imageKey; mosaic = null;
                        _mapImageLoading = null; _mapImageError = null;
                    }
                    if (!IsDisposed) BeginInvoke((Action)Invalidate);
                }
                catch (Exception ex)
                {
                    mosaic?.Dispose();
                    lock (_mapImageSync) { _mapImageLoading = null; _mapImageError = ex.GetType().Name; }
                    if (!IsDisposed) BeginInvoke((Action)Invalidate);
                }
            });
        }

        private int SelectTileZoom(RadarMapData map, float viewSize)
        {
            if (map.TileMaxZoom < map.TileMinZoom) return map.TileMinZoom;
            // Pick enough source pixels to cover the viewport while keeping
            // startup bounded.  zoom 2 gives a complete 1024x1024 map for
            // the calculator's 16x16 tileBounds; zoom 3 is used for larger
            // windows or when the user zooms in.  Higher levels are loaded
            // only if the map metadata explicitly permits them.
            var fitted = Math.Max(1f, viewSize * Math.Max(1f, _settings.RadarZoom));
            var requested = (int)Math.Ceiling(Math.Log(fitted / Math.Max(1, map.TileSize), 2));
            requested = Math.Clamp(requested, map.TileMinZoom, Math.Min(map.TileMaxZoom, 3));
            return requested;
        }

        private async Task<(int X, int Y, byte[]? Bytes)> LoadTileAsync(RadarMapData map, int zoom, int x, int y)
        {
            try
            {
                var cacheRoot = Path.Combine(AppContext.BaseDirectory, "maps", "tile-cache", map.Id, $"z{zoom}");
                Directory.CreateDirectory(cacheRoot);
                var cacheFile = Path.Combine(cacheRoot, $"{x}_{y}.{map.TileExtension}");
                byte[] bytes;
                if (File.Exists(cacheFile))
                    bytes = await File.ReadAllBytesAsync(cacheFile).ConfigureAwait(false);
                else
                {
                    // Keep compatibility with the old single-tile cache for
                    // zoom 0, while all newly downloaded data is namespaced
                    // by map/zoom/tile and comes directly from the JSON path.
                    var legacy = Path.Combine(AppContext.BaseDirectory, "maps", "tile-cache", $"{map.Id}-z{zoom}-{x}-{y}.{map.TileExtension}");
                    bytes = File.Exists(legacy)
                        ? await File.ReadAllBytesAsync(legacy).ConfigureAwait(false)
                        : await _http.GetByteArrayAsync($"{map.TilePath!.TrimEnd('/')}/zoom_{zoom}/{x}_{y}.{map.TileExtension}").ConfigureAwait(false);
                    await File.WriteAllBytesAsync(cacheFile, bytes).ConfigureAwait(false);
                }
                return (x, y, bytes);
            }
            catch
            {
                return (x, y, null);
            }
        }

        private static double ResolveWorldOrigin(float gameCoordinate, double minMap, double maxMap, double fallback)
        {
            if (!float.IsFinite(gameCoordinate)) return fallback;
            // Wardogs levels are placed on UE world-partition boundaries. Pick
            // the nearest 4096 UU boundary that puts the live camera inside
            // the selected map bounds; this handles maps with different level
            // origins without hard-coding one origin globally.
            var midpoint = (minMap + maxMap) * 0.5;
            var candidate = Math.Round((gameCoordinate - midpoint * 100.0) / 4096.0) * 4096.0;
            var lower = gameCoordinate - maxMap * 100.0;
            var upper = gameCoordinate - minMap * 100.0;
            if (candidate >= lower - 4096 && candidate <= upper + 4096) return candidate;
            return fallback;
        }

        private static double RefineOriginFromContacts(IEnumerable<float> coordinates, double anchor, float cameraCoordinate, double minMap, double maxMap, double unitsPerMap)
        {
            var values = coordinates.Where(float.IsFinite).ToArray();
            if (values.Length == 0) return anchor;
            var best = anchor; var bestScore = -1; var bestDistance = double.MaxValue;
            for (var step = -12; step <= 12; step++)
            {
                var candidate = anchor + step * 4096.0;
                // The live camera must remain inside the selected map.  This
                // prevents a dense set of streamed actors from pulling the
                // origin to another world-partition cell (the old behavior
                // produced origins such as -823296 and left most contacts
                // outside the map).
                var cameraMapValue = (cameraCoordinate - candidate) / unitsPerMap;
                if (cameraMapValue < minMap || cameraMapValue > maxMap) continue;
                var score = values.Count(value =>
                {
                    var mapValue = (value - candidate) / unitsPerMap;
                    return mapValue >= minMap && mapValue <= maxMap;
                });
                var distance = Math.Abs(step);
                if (score > bestScore || (score == bestScore && distance < bestDistance))
                {
                    best = candidate; bestScore = score; bestDistance = distance;
                }
            }
            return bestScore > 0 ? best : anchor;
        }

        private static float ScreenHeadingYaw(float yaw, RadarMapData map)
        {
            // Convert a UE world forward vector into the same screen basis as
            // WorldToPoint: X follows the map X sign and screen Y is inverted
            // because GDI's origin is top-left.  atan2 keeps all four sign
            // combinations correct (a simple +/- yaw shortcut is wrong when
            // both axes are mirrored).
            var radians = yaw * Math.PI / 180.0;
            var sx = map.WorldAxisSignX < 0 ? -1.0 : 1.0;
            var sy = map.WorldAxisSignY < 0 ? -1.0 : 1.0;
            var screen = Math.Atan2(-sy * Math.Sin(radians), sx * Math.Cos(radians)) * 180.0 / Math.PI;
            while (screen > 180.0) screen -= 360.0;
            while (screen < -180.0) screen += 360.0;
            return (float)screen;
        }

        private void DrawContact(Graphics g, PointF p, float yaw, Color color, float radius, float mapYaw, bool forceUp, float radarRotationDeg, RadarMapData map)
        {
            using var fill = new SolidBrush(color); using var outline = new Pen(Color.Black, 1);
            g.FillEllipse(fill, p.X - radius, p.Y - radius, radius * 2, radius * 2); g.DrawEllipse(outline, p.X - radius, p.Y - radius, radius * 2, radius * 2);
            var angle = (ScreenHeadingYaw(yaw, map) - ScreenHeadingYaw(mapYaw, map) + radarRotationDeg) * Math.PI / 180.0;
            if (forceUp) angle = -Math.PI / 2;
            using var heading = new Pen(Color.White, 1.5f);
            g.DrawLine(heading, p, new PointF(p.X + (float)Math.Cos(angle) * radius * 2.8f, p.Y + (float)Math.Sin(angle) * radius * 2.8f));
        }

        private static void DrawGrid(Graphics g, PointF origin, float w, float h, float worldW, float worldH, float scale)
        {
            using var pen = new Pen(Color.FromArgb(32, 150, 185, 145), 1);
            using var text = new SolidBrush(Color.FromArgb(150, 205, 220, 200));
            var step = worldW > 120 ? 10 : 5;
            var captionFont = SystemFonts.CaptionFont ?? SystemFonts.DefaultFont;
            for (var x = 0f; x <= worldW; x += step) { var sx = origin.X + x * scale; g.DrawLine(pen, sx, origin.Y, sx, origin.Y + h); g.DrawString($"{x:0}", captionFont, text, sx + 2, origin.Y + h - 17); }
            for (var y = 0f; y <= worldH; y += step) { var sy = origin.Y + y * scale; g.DrawLine(pen, origin.X, sy, origin.X + w, sy); }
        }

        private static void DrawMapDecorations(Graphics g, RadarMapData map, PointF origin, float scale)
        {
            foreach (var polygon in map.Polygons)
            {
                if (polygon.Count < 3) continue;
                var points = polygon.Select(p => new PointF(origin.X + (float)(p.X / map.CoordinateMetersPerUnit - map.RenderMinX) * scale, origin.Y + (float)(map.RenderMaxY - p.Y / map.CoordinateMetersPerUnit) * scale)).ToArray();
                using var pen = new Pen(Color.FromArgb(150, 210, 170, 90), 1.5f); pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash; g.DrawPolygon(pen, points);
            }
            foreach (var marker in map.Markers)
            {
                var markerX = marker.X / map.CoordinateMetersPerUnit;
                var markerY = marker.Y / map.CoordinateMetersPerUnit;
                var p = new PointF(origin.X + (float)(markerX - map.RenderMinX) * scale, origin.Y + (float)(map.RenderMaxY - markerY) * scale);
                using var b = new SolidBrush(Color.FromArgb(180, 220, 190, 80)); g.FillRectangle(b, p.X - 2, p.Y - 2, 4, 4);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_mapImageSync) { _mapImage?.Dispose(); _scaledMapImage?.Dispose(); _mapImage = null; _scaledMapImage = null; }
                _http.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class RadarMapData
    {
        public string Id { get; init; } = ""; public string Name { get; init; } = "";
        public double MinX { get; init; } public double MaxX { get; init; } public double MinY { get; init; } public double MaxY { get; init; }
        public double RenderMinX => TileMinX ?? MinX; public double RenderMaxX => TileMaxX ?? MaxX;
        public double RenderMinY => TileMinY ?? MinY; public double RenderMaxY => TileMaxY ?? MaxY;
        public double CoordinateMetersPerUnit { get; init; } = 100;
        public double WorldOriginX { get; init; } = -614400;
        public double WorldOriginY { get; init; } = -614400;
        public bool HasWorldTransform { get; init; }
        public double WorldOffsetMetersX { get; init; }
        public double WorldOffsetMetersY { get; init; }
        public double WorldAxisSignX { get; init; } = 1;
        public double WorldAxisSignY { get; init; } = 1;
        public string? TilePath { get; init; } public int TileSize { get; init; } = 256;
        public int TileMinZoom { get; init; } public int TileMaxZoom { get; init; }
        public string TileExtension { get; init; } = "webp";
        public double? TileMinX { get; init; } public double? TileMaxX { get; init; }
        public double? TileMinY { get; init; } public double? TileMaxY { get; init; }
        public List<(double X, double Y, string Label)> Markers { get; } = new();
        public List<List<(double X, double Y)>> Polygons { get; } = new();

        public static IReadOnlyList<RadarMapData> LoadAll(string configured)
        {
            var dirs = new[] { configured, Path.Combine(AppContext.BaseDirectory, "maps"), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "wardogs-calculator-main", "maps")), @"I:\wardogs\wardogs-calculator-main\maps" }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var result = Directory.GetFiles(dir, "*.json").Where(x => !Path.GetFileName(x).Equals("index.json", StringComparison.OrdinalIgnoreCase)).Select(Parse).Where(x => x is not null).Cast<RadarMapData>().OrderBy(x => x.Name).ToArray();
                    if (result.Length > 0) return result;
                }
                catch { }
            }
            return Array.Empty<RadarMapData>();
        }

        private static RadarMapData? Parse(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path)); var root = doc.RootElement;
                var b = root.GetProperty("bounds");
                var originX = root.TryGetProperty("worldOriginX", out var ox) ? ox.GetDouble() : -614400;
                var originY = root.TryGetProperty("worldOriginY", out var oy) ? oy.GetDouble() : -614400;
                string? tilePath = null; var tileSize = 256; var tileMinZoom = 0; var tileMaxZoom = 0; var tileExtension = "webp";
                if (root.TryGetProperty("tiles", out var tiles) && tiles.ValueKind == JsonValueKind.Object)
                {
                    tilePath = tiles.TryGetProperty("path", out var tp) ? tp.GetString() : null;
                    tileSize = tiles.TryGetProperty("tileSize", out var ts) ? ts.GetInt32() : 256;
                    tileMinZoom = tiles.TryGetProperty("minZoom", out var tz0) ? tz0.GetInt32() : 0;
                    tileMaxZoom = tiles.TryGetProperty("maxZoom", out var tz1) ? tz1.GetInt32() : tileMinZoom;
                    tileExtension = tiles.TryGetProperty("extension", out var te) ? te.GetString() ?? "webp" : "webp";
                }
                double? tileMinX = null, tileMaxX = null, tileMinY = null, tileMaxY = null;
                if (root.TryGetProperty("tileBounds", out var tb) && tb.ValueKind == JsonValueKind.Object)
                {
                    tileMinX = tb.GetProperty("minX").GetDouble(); tileMaxX = tb.GetProperty("maxX").GetDouble();
                    tileMinY = tb.GetProperty("minY").GetDouble(); tileMaxY = tb.GetProperty("maxY").GetDouble();
                }
                var id = root.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(path);
                var coordinateMetersPerUnit = root.TryGetProperty("coordinateMetersPerUnit", out var c) ? c.GetDouble() : 100;
                // These are the calibrated transforms documented by the
                // calculator terrain data.  Absolute UE positions are cm;
                // offsets below are metres at GameX/GameY == 0.
                var hasWorldTransform = false;
                var worldOffsetMetersX = 0.0; var worldOffsetMetersY = 0.0;
                var worldAxisSignX = 1.0; var worldAxisSignY = 1.0;
                if (id.Equals("bakurani", StringComparison.OrdinalIgnoreCase))
                {
                    hasWorldTransform = true; worldOffsetMetersX = -16320; worldOffsetMetersY = -4080; worldAxisSignY = -1;
                }
                else if (id.Equals("ozeti", StringComparison.OrdinalIgnoreCase))
                {
                    hasWorldTransform = true; worldOffsetMetersX = -16160; worldOffsetMetersY = 160; worldAxisSignY = -1;
                }
                else if (id.Equals("zestafona", StringComparison.OrdinalIgnoreCase))
                {
                    // Zestafona uses the Europe Landscape capture.  Its
                    // terrain manifest has globalQuadOffsetX=0 and
                    // globalQuadOffsetY=8160, while the Europe Landscape
                    // root is (-16322 m, -16322 m).  With 2 m per landscape
                    // quad this gives GameX=0 -> -16322 m and
                    // GameY=0 -> -2 m; +GameY runs opposite map north.
                    // Keeping this fixed avoids the old 4096-uu partition
                    // heuristic jumping the player by ~40 map units.
                    hasWorldTransform = true; worldOffsetMetersX = -16322; worldOffsetMetersY = -2; worldAxisSignY = -1;
                }
                var result = new RadarMapData { Id = id, Name = root.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(path), MinX = b.GetProperty("minX").GetDouble(), MaxX = b.GetProperty("maxX").GetDouble(), MinY = b.GetProperty("minY").GetDouble(), MaxY = b.GetProperty("maxY").GetDouble(), CoordinateMetersPerUnit = coordinateMetersPerUnit, WorldOriginX = originX, WorldOriginY = originY, HasWorldTransform = hasWorldTransform, WorldOffsetMetersX = worldOffsetMetersX, WorldOffsetMetersY = worldOffsetMetersY, WorldAxisSignX = worldAxisSignX, WorldAxisSignY = worldAxisSignY, TilePath = tilePath, TileSize = tileSize, TileMinZoom = tileMinZoom, TileMaxZoom = tileMaxZoom, TileExtension = tileExtension, TileMinX = tileMinX, TileMaxX = tileMaxX, TileMinY = tileMinY, TileMaxY = tileMaxY };
                if (root.TryGetProperty("markers", out var markers)) foreach (var m in markers.EnumerateArray()) result.Markers.Add((m.GetProperty("x").GetDouble(), m.GetProperty("y").GetDouble(), m.TryGetProperty("label", out var l) ? l.GetString() ?? "" : ""));
                if (root.TryGetProperty("polygons", out var polys)) foreach (var poly in polys.EnumerateArray()) { var points = new List<(double, double)>(); foreach (var p in poly.GetProperty("points").EnumerateArray()) points.Add((p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble())); result.Polygons.Add(points); }
                return result;
            }
            catch { return null; }
        }
    }

    // ImGui.NET is used for the menu state/context; the WinForms host keeps
    // the menu independent from the click-through full-screen paint surface.
    // Insert toggles this compact control window while the overlay remains
    // fully black and click-through.
    private sealed class ImGuiMenuForm : Form
    {
        private readonly EspSettings _settings;
        private readonly IntPtr _imguiContext;
        private readonly CheckBox _enabled;
        private readonly CheckBox _players;
        private readonly CheckBox _vehicles;
        private readonly CheckBox _friendlyVehicles;
        private readonly CheckBox _enemyVehicles;
        private readonly CheckBox _teammates;
        private readonly CheckBox _enemies;
        private readonly CheckBox _downed;
        private readonly CheckBox _names;
        private readonly CheckBox _distance;
        private readonly CheckBox _healthBar;
        private readonly CheckBox _skeleton;
        private readonly CheckBox _weapons;
        private readonly NumericUpDown _readInterval;
        private readonly NumericUpDown _renderFps;
        private readonly NumericUpDown _sourceRefresh;
        private readonly NumericUpDown _vehicleRefresh;
        private readonly NumericUpDown _logLevel;
        private Point _dragStart;
        private bool _dragging;
        private readonly PreviewPanel _preview;

        private const string PreviewGlb = @"I:\FModel-dev\FModel\bin\x64\Debug\net10.0-windows\win-x64\Output\Exports\Wardogs\Content\Characters\Player\_Presets\Lonestar\03\SK\SK_Preset_Lonestar_03.glb";

        public ImGuiMenuForm(EspSettings settings)
        {
            _settings = settings;
            _imguiContext = ImGui.CreateContext();
            Text = "Wardogs ESP 控制台";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(18, 20, 26);
            ForeColor = Color.White;
            ClientSize = new Size(1100, 840);

            var header = new Panel { Location = new Point(0, 0), Size = new Size(1100, 66), BackColor = Color.FromArgb(30, 34, 45), Cursor = Cursors.SizeAll };
            var title = new Label { Text = "WARDOGS  /  ESP 控制台", AutoSize = true,
                Location = new Point(22, 12), Font = new Font("Segoe UI", 13, FontStyle.Bold), ForeColor = Color.White };
            var subtitle = new Label { Text = "GPU 覆盖层设置   •   按 INSERT 隐藏   •   拖动顶部标题栏移动窗口", AutoSize = true,
                Location = new Point(24, 38), ForeColor = Color.FromArgb(160, 170, 185) };
            header.Controls.Add(title); header.Controls.Add(subtitle); Controls.Add(header);
            header.MouseDown += BeginDrag; header.MouseMove += DragWindow; header.MouseUp += EndDrag;
            title.MouseDown += BeginDrag; title.MouseMove += DragWindow; title.MouseUp += EndDrag;
            subtitle.MouseDown += BeginDrag; subtitle.MouseMove += DragWindow; subtitle.MouseUp += EndDrag;
            AddSidebar();
            AddSection("显示设置", 78, 150);
            _enabled = AddCheck("启用 ESP", settings.Enabled, 108);
            _players = AddCheck("显示玩家", settings.ShowPlayers, 136);
            _vehicles = AddCheck("显示载具", settings.ShowVehicles, 164);
            _teammates = AddCheck("显示队友", settings.ShowTeammates, 192);
            _enemies = AddCheck("显示敌人", settings.ShowEnemies, 220);
            _downed = AddCheck("显示倒地", settings.ShowDowned, 248);
            _names = AddCheck("显示名称/状态", settings.ShowNames, 276);
            _distance = AddCheck("显示距离", settings.ShowDistance, 304);
            _healthBar = AddCheck("显示血条", settings.ShowHealthBar, 332);
            _skeleton = AddCheck("显示玩家骨骼", settings.ShowSkeleton, 360);
            _weapons = AddCheck("显示手持武器", settings.ShowWeapons, 444);
            _friendlyVehicles = AddCheck("显示友方载具", settings.ShowFriendlyVehicles, 388);
            _enemyVehicles = AddCheck("显示敌方载具", settings.ShowEnemyVehicles, 416);
            AddSection("性能与颜色", 476, 286);
            AddNumeric("读取间隔 (ms)  [0 = 不限速]", settings.ReadIntervalMs, 506, 0, 1000, v => _settings.ReadIntervalMs = v, out _readInterval);
            AddNumeric("ESP FPS  [0 = 不限速]", settings.RenderFpsLimit, 540, 0, 1000, v => _settings.RenderFpsLimit = v, out _renderFps);
            AddColorButton("敌方颜色", settings.EnemyColorArgb, 580, c => _settings.EnemyColorArgb = c.ToArgb());
            AddColorButton("友方颜色", settings.FriendlyColorArgb, 580, c => _settings.FriendlyColorArgb = c.ToArgb(), 175);
            AddColorButton("敌方载具", settings.EnemyVehicleColorArgb, 616, c => _settings.EnemyVehicleColorArgb = c.ToArgb());
            AddColorButton("友方载具", settings.FriendlyVehicleColorArgb, 616, c => _settings.FriendlyVehicleColorArgb = c.ToArgb(), 175);
            AddColorButton("倒地颜色", settings.DownedColorArgb, 652, c => _settings.DownedColorArgb = c.ToArgb());
            AddColorButton("未知颜色", settings.UnknownColorArgb, 652, c => _settings.UnknownColorArgb = c.ToArgb(), 175);
            AddNumeric("关卡源刷新 (ms)", settings.SourceRefreshMs, 688, 250, 60000, v => _settings.SourceRefreshMs = v, out _sourceRefresh);
            AddNumeric("全局载具刷新 (ms)", settings.VehicleRefreshMs, 722, 250, 60000, v => _settings.VehicleRefreshMs = v, out _vehicleRefresh);
            AddNumeric("日志级别 (0关/1错误/2常规/3详细)", settings.LogLevel, 756, 0, 3, v => _settings.LogLevel = v, out _logLevel);
            _preview = new PreviewPanel(PreviewGlb) { Location = new Point(790, 78), Size = new Size(292, 610), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Controls.Add(_preview);
            FormClosed += (_, _) => ImGui.DestroyContext(_imguiContext);
        }

        private void AddSidebar()
        {
            var side = new Panel { Location = new Point(14, 78), Size = new Size(190, 610), BackColor = Color.FromArgb(17, 19, 26), BorderStyle = BorderStyle.FixedSingle };
            var items = new[] { "◎  ESP 总览", "◉  视觉设置", "⚗  外观颜色", "⚙  配置保存", "⚙  菜单设置" };
            for (var i = 0; i < items.Length; i++)
            {
                var label = new Label { Text = items[i], AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
                    Location = new Point(10, 12 + i * 48), Size = new Size(168, 38), Padding = new Padding(10, 0, 0, 0),
                    ForeColor = i == 0 ? Color.White : Color.FromArgb(130, 145, 165), BackColor = i == 0 ? Color.FromArgb(35, 38, 50) : Color.Transparent };
                side.Controls.Add(label);
            }
            Controls.Add(side);
        }

        private void BeginDrag(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true; _dragStart = e.Location;
        }
        private void DragWindow(object? sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var screen = PointToScreen(e.Location);
            Location = new Point(screen.X - _dragStart.X, screen.Y - _dragStart.Y);
        }
        private void EndDrag(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _dragging = false; }

        private Panel AddSection(string text, int y, int height)
        {
            var panel = new Panel { Location = new Point(220, y), Size = new Size(550, height),
                BackColor = Color.FromArgb(24, 27, 36), BorderStyle = BorderStyle.FixedSingle };
            var label = new Label { Text = text, AutoSize = true, Location = new Point(12, 6),
                Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(125, 190, 255) };
            panel.Controls.Add(label); Controls.Add(panel); return panel;
        }

        private void AddNumeric(string text, int value, int y, int min, int max,
            Action<int> changed, out NumericUpDown control)
        {
            var label = new Label { Text = text, AutoSize = true, Location = new Point(240, y + 4), ForeColor = Color.Gainsboro };
            var numeric = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, Value = Math.Clamp(value, min, max),
                Location = new Point(650, y), Width = 90, BackColor = Color.FromArgb(35, 39, 50), ForeColor = Color.White };
            control = numeric;
            numeric.ValueChanged += (_, _) => { changed((int)numeric.Value); _settings.Save(); };
            Controls.Add(label); Controls.Add(numeric);
        }

        private void AddColorButton(string text, int argb, int y, Action<Color> changed, int x = 240)
        {
            var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(135, 28),
                FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(argb) };
            button.FlatAppearance.BorderColor = Color.FromArgb(90, 100, 120);
            button.Click += (_, _) =>
            {
                using var dialog = new ColorDialog { Color = button.BackColor, FullOpen = true };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                button.BackColor = dialog.Color; changed(dialog.Color); _settings.Save();
            };
            Controls.Add(button);
        }

        private CheckBox AddCheck(string text, bool value, int y)
        {
            var check = new CheckBox
            {
                Text = text, Checked = value, AutoSize = true,
                Location = new Point(240, y), ForeColor = Color.White,
                BackColor = Color.Transparent
            };
            check.CheckedChanged += (_, _) =>
            {
                _settings.Enabled = _enabled.Checked;
                _settings.ShowPlayers = _players.Checked;
                _settings.ShowVehicles = _vehicles.Checked;
                _settings.ShowFriendlyVehicles = _friendlyVehicles.Checked;
                _settings.ShowEnemyVehicles = _enemyVehicles.Checked;
                _settings.ShowTeammates = _teammates.Checked;
                _settings.ShowEnemies = _enemies.Checked;
                _settings.ShowDowned = _downed.Checked;
                _settings.ShowNames = _names.Checked;
                _settings.ShowDistance = _distance.Checked;
                _settings.ShowHealthBar = _healthBar.Checked;
                _settings.ShowSkeleton = _skeleton.Checked;
                _settings.ShowWeapons = _weapons.Checked;
                _settings.Save();
            };
            Controls.Add(check);
            return check;
        }

        private sealed class PreviewPanel : Panel
        {
            private readonly string _path;
            private Bitmap? _preview;
            public PreviewPanel(string path) { _path = path; BackColor = Color.FromArgb(12, 14, 18); BorderStyle = BorderStyle.FixedSingle; DoubleBuffered = true; LoadPreview(); }
            private void LoadPreview()
            {
                try
                {
                    if (!File.Exists(_path)) return;
                    var model = ModelRoot.Load(_path, new ReadSettings());
                    var points = new List<Vector3>();
                    foreach (var mesh in model.LogicalMeshes)
                        foreach (var prim in mesh.Primitives)
                            if (prim.GetVertexAccessor("POSITION") is { } pos)
                                points.AddRange(pos.AsVector3Array());
                    if (points.Count == 0) return;
                    Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
                    foreach (var p in points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                    var size = Vector3.Max(max - min, new Vector3(0.001f));
                    _preview = new Bitmap(280, 570, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(_preview); g.Clear(Color.FromArgb(12, 14, 18));
                    using var pen = new Pen(Color.FromArgb(90, 220, 205), 1.2f);
                    foreach (var p in points.Where((_, i) => i % Math.Max(1, points.Count / 1800) == 0))
                    {
                        var x = 140 + (p.X - (min.X + max.X) / 2) / size.X * 180;
                        var y = 540 - (p.Z - min.Z) / size.Z * 500;
                        if (x >= 0 && x < 280 && y >= 0 && y < 570) g.DrawEllipse(pen, x, y, 1.5f, 1.5f);
                    }
                }
                catch { _preview = null; }
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e); using var title = new Font("Segoe UI", 10, FontStyle.Bold);
                e.Graphics.DrawString("ESP 预览模型", title, Brushes.White, 14, 14);
                if (_preview is not null) e.Graphics.DrawImageUnscaled(_preview, 6, 36);
                else e.Graphics.DrawString("模型预览不可用", Font, Brushes.Gray, 14, 48);
            }
            protected override void Dispose(bool disposing) { if (disposing) _preview?.Dispose(); base.Dispose(disposing); }
        }
    }
}







