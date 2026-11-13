using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using System;
using System.IO.MemoryMappedFiles;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SDAE.Unity;

/// <summary>
/// Deterministic 1 kHz BCI-to-Unity kinematic actuator and telemetry engine built on the
/// SDAE (State-Driven Architecture Engine) bitmask state machine.
///
/// Architectural Highlights:
/// 1. Lock-Free 96-Byte Shared-Memory IPC ("sdae_bci_memory") decoupled from GPU frame rate.
/// 2. Dedicated High-Priority 1 kHz Master Clock Worker Thread with sub-millisecond phase alignment.
/// 3. PlayerLoop EarlyUpdate injection with O(1) De Bruijn bit-scan edge dispatch and strict Zero-GC HUD.
/// 4. Native Windows QueryPerformanceCounter (QPC) nanosecond synchronization matching Python's perf_counter_ns().
/// </summary>
public class Apple : MonoBehaviour
{
    public static Apple Instance { get; private set; }

    // =========================================================================
    // NATIVE HARDWARE CLOCK P/INVOKE (SHARED EPOCH WITH PYTHON PERF_COUNTER_NS)
    // =========================================================================
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);
#endif

    private static long qpcFrequency = 0L;
    private static bool useNativeQpc = false;

    // =========================================================================
    // STATIC SESSION CONFIGURATION (POPULATED BY MENU.CS OR BENCHMARK R)
    // =========================================================================
    public static float speedPercentage = 100f;
    public static float decisionPercentile = 50f;
    public static int subjectNumber = 1;
    public static bool trainingMode = true;
    public static bool showTelemetryHUD = true;
    private static bool hudVisibilityOverriddenByMenu = false;

    // Legacy static fields synchronized with the deterministic state mask
    public static float fallSpeed = 2f;
    public static bool shouldMove = false;
    public static bool resetApple = false;
    public static bool resettedApple = true;
    public static int positionFactor = 1; // 1 = Left, 2 = Right
    public static int correct = 1;

    // =========================================================================
    // SDAE 32-BIT BITMASK STATE ENGINE DEFINITIONS
    // =========================================================================
    public const uint MASK_NONE = 0u;
    public const uint MASK_CONFIG_READY = 1u << 0; // 0x001: Session parameters published to RAM
    public const uint MASK_TRIAL_TRIGGERED = 1u << 1; // 0x002: Trial trigger received from Python
    public const uint MASK_PREPARING_HANDS = 1u << 2; // 0x004: Bilateral hand opening in progress (Zero-GC)
    public const uint MASK_HANDS_OPEN = 1u << 3; // 0x008: Both hands verified open
    public const uint MASK_FALLING = 1u << 4; // 0x010: 1 kHz master clock stepping trajectory
    public const uint MASK_DECISION_READY = 1u << 5; // 0x020: Classifier decision received in RAM
    public const uint MASK_TRIGGER_FIRED = 1u << 6; // 0x040: Decision percentile reached (hand grasp)
    public const uint MASK_CONTACT_DONE = 1u << 7; // 0x080: End of prebaked trajectory reached
    public const uint MASK_BENCHMARK_R = 1u << 8; // 0x100: Randomized stress-test benchmark active

    private const uint REQ_START_FALL = MASK_CONFIG_READY | MASK_TRIAL_TRIGGERED | MASK_HANDS_OPEN;
    private const uint CLEAR_TRIAL_BITS = ~(MASK_TRIAL_TRIGGERED | MASK_PREPARING_HANDS | MASK_HANDS_OPEN |
                                            MASK_FALLING | MASK_DECISION_READY | MASK_TRIGGER_FIRED | MASK_CONTACT_DONE);

    /// <summary>
    /// Deterministic Next-Mask Lookup Table (LUT) for branchless state transitions.
    /// </summary>
    private static readonly uint[] NEXT_MASK_LUT = new uint[]
    {
        MASK_CONFIG_READY,                                                           // [0] Standby
        MASK_CONFIG_READY | MASK_TRIAL_TRIGGERED,                                    // [1] Trial Triggered
        MASK_CONFIG_READY | MASK_FALLING,                                            // [2] 1 kHz Falling
        MASK_CONFIG_READY | MASK_FALLING | MASK_DECISION_READY | MASK_TRIGGER_FIRED, // [3] Trigger Fired
        MASK_CONFIG_READY | MASK_CONTACT_DONE                                        // [4] Contact Done
    };

    /// <summary>
    /// 32-bit De Bruijn sequence table for O(1) lowest-set-bit index resolution.
    /// </summary>
    private static readonly int[] DE_BRUIJN_BIT_POS = new int[]
    {
        0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8,
        31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9
    };
    private Action<uint>[] edgeDispatchTable;

    // Atomic state shared between the 1 kHz Worker Thread and Main Thread EarlyUpdate
    private int sharedStateMask = 0;
    private int sharedStepIndex = 0;
    private int sharedTotalSteps = 3000;
    private int sharedTriggerStep = 1500;
    private int sharedTargetHand = 1;
    private int sharedDecision = 0;
    private uint lastObservedEarlyMask = 0;

    // Zero-GC hand opening state tracked inside EarlyUpdate
    private int handPrepFrameCounter = 0;
    private float handPrepElapsedSec = 0f;

    // Atomic hardware telemetry metrics published to RAM at trial completion
    private float atomicIpcTriggerLatencyMs = 0f;
    private float atomicDecisionTransportMs = 0f;
    private float atomicAnticipationMarginMs = 0f;
    private float atomicActualKinematicMs = 0f;
    private float atomicClockJitterMs = 0f;
    private int atomicClockHz = 0;
    private int sharedMaxFrameBurst = 0;
    private float sharedMeanTrialFps = 0f;

    // Dedicated 1 kHz master clock worker thread
    private Thread sdaeClockThread;
    private volatile bool sdaeThreadRunning = false;

    /// <summary>
    /// Marker struct for PlayerLoop EarlyUpdate subsystem registration.
    /// </summary>
    private struct SdaeEarlyUpdateSystem { }

    [Header("Engine & Target Framerate Setup")]
    public int targetFramerate = 120;
    public bool lockVSyncOff = true;
    [Tooltip("Shadow rendering cutoff distance to protect GPU frame time")]
    public float maxShadowDistance = 20.0f;

    [Header("SDAE Zero-GC Step Engine Setup")]
    [Tooltip("Nominal fall duration in seconds at 100% speed (3000 ms)")]
    public float targetTrialDurationSec = 3.0f;
    [Tooltip("Fixed kinematic grid rate in Hz (1 step = 1.00 ms)")]
    public int sdaeTickRateHz = 1000;
    public float dropYOffset = 6.0f;
    public const int MAX_STEPS = 30005;

    [Header("Benchmark Stress-Test Setup (Key R)")]
    public int benchmarkTrialCount = 10;
    public float minRandomSpeedPct = 10f;
    public float maxRandomSpeedPct = 100f;
    public float minRandomPercentile = 10f;
    public float maxRandomPercentile = 50f;

    [Header("Visual Feedback Setup (Zero-GC Material Property Block)")]
    public bool fadeOnError = true;
    public float fadeDuration = 1.2f;
    public GameObject opaqueAppleBackup;
    public GameObject transparentAppleBackup;

    [Header("Hand Animation Timings")]
    public float maxOpenWaitTimeout = 2.5f;

    [Header("Hands Animators (Index 1 = Left, Index 2 = Right)")]
    public Animator leftHandAnimator;
    public Animator rightHandAnimator;

    [Header("HUD Telemetry Config")]
    [Tooltip("Default visibility of the on-screen diagnostic HUD (can be overridden by Menu.cs)")]
    public bool showHUD = true;
    [Tooltip("HUD text refresh interval in seconds to prevent per-frame OnGUI string allocations")]
    public float hudRefreshIntervalSec = 0.15f;

    // Branchless actuator dispatch tables
    private Vector3[] handBasePositions;
    private Animator[] handAnimators;
    private DynamicHandScript[] handCaches;
    private Action[] openHandActions;
    private Action[] closeHandActions;
    private Action[] errorFadeActions;

    // Pre-baked 1 kHz kinematic trajectory buffer (Zero Heap Allocations)
    private readonly Vector3[] prebakedTrajectory = new Vector3[MAX_STEPS];
    private float calculatedTargetY = 0f;
    private float startYPosition = 0f;

    // Render telemetry counters
    private int lastRenderedStep = 0;
    private int lastFrameStepDelta = 0;
    private int maxStepsInSingleFrame = 0;
    private float lastClockJitterMs = 0f;
    private float trialFpsAccumulator = 0f;
    private int trialFpsFrameCount = 0;

    // Randomized Benchmark state (Key R)
    private int benchmarkCompletedTrials = 0;
    private string benchmarkStatusBanner = "";

    private Coroutine fadeCoroutine = null;

    // Zero-GC Material Property Block cache
    private Renderer appleRenderer;
    private MaterialPropertyBlock mpb;
    private Material opaqueSharedMat;
    private Material transparentSharedMat;
    private int colorPropertyId;

    // Lock-Free 96-Byte Shared-Memory IPC
    private MemoryMappedFile mmf;
    private MemoryMappedViewAccessor accessor;
    private const int PAYLOAD_SIZE = 96;

    // Rolling HUD telemetry buffers
    private const int MAX_TELEMETRY_LOGS = 100;
    private readonly float[] ipcLatencyBuffer = new float[MAX_TELEMETRY_LOGS];
    private readonly float[] jitterBuffer = new float[MAX_TELEMETRY_LOGS];
    private readonly string[] logHistoryBuffer = new string[5];
    private int logHistoryIndex = 0;
    private int trialCounter = 0;
    private int correctTrials = 0;

    private float avgIpcLatencyMs = 0f;
    private float maxIpcLatencyMs = 0f;
    private float avgClockJitterMs = 0f;
    private float stdDevClockJitterMs = 0f;

    // Cached GUI styles and pre-formatted strings (Zero-GC per-frame OnGUI)
    private GUIStyle headerStyle;
    private GUIStyle modeStyle;
    private GUIStyle benchmarkStyle;
    private GUIStyle metricStyle;
    private GUIStyle standbyStyle;
    private GUIStyle logStyle;
    private bool guiStylesInitialized = false;

    private volatile bool hudDirty = true;
    private float nextHudRefreshTime = 0f;
    private string cachedSubjectLine = "";
    private string cachedMaskLine = "";
    private string cachedAccuracyLine = "";
    private string cachedIpcLine = "";
    private string cachedTransportLine = "";
    private string cachedJitterLine = "";
    private string cachedGpuLine = "";

    private interface DynamicHandScript
    {
        bool IsFullyOpen { get; }
        string OpenStateName { get; }
        void Open();
        void Close();
    }

    private class Hand1Adapter : DynamicHandScript
    {
        private readonly hand1_script h;
        public Hand1Adapter(hand1_script h) => this.h = h;
        public bool IsFullyOpen => h != null && h.isFullyOpen;
        public string OpenStateName => h?.openStateName ?? "Open";
        public void Open() => h?.OpenHand();
        public void Close() => h?.CloseHand();
    }

    private class Hand2Adapter : DynamicHandScript
    {
        private readonly hand2_script h;
        public Hand2Adapter(hand2_script h) => this.h = h;
        public bool IsFullyOpen => h != null && h.isFullyOpen;
        public string OpenStateName => h?.openStateName ?? "Open";
        public void Open() => h?.OpenHand();
        public void Close() => h?.CloseHand();
    }

    // =========================================================================
    // PUBLIC MENU API FOR TELEMETRY HUD VISIBILITY CONTROL
    // =========================================================================

    /// <summary>
    /// Static method callable from Menu.cs (before or after scene load) to show or hide
    /// the on-screen diagnostic telemetry HUD without affecting underlying 1 kHz logging.
    /// </summary>
    public static void SetTelemetryHUDVisible(bool isVisible)
    {
        hudVisibilityOverriddenByMenu = true;
        showTelemetryHUD = isVisible;
        if (Instance != null)
        {
            Instance.showHUD = isVisible;
            Instance.hudDirty = true;
        }
    }

    /// <summary>
    /// Instance wrapper compatible with Unity UI Toggle.onValueChanged(bool) events.
    /// </summary>
    public void SetHUDVisible(bool isVisible)
    {
        SetTelemetryHUDVisible(isVisible);
    }

    /// <summary>
    /// Instance helper to toggle the diagnostic HUD on/off from a UI Button.
    /// </summary>
    public void ToggleHUDVisible()
    {
        SetTelemetryHUDVisible(!showTelemetryHUD);
    }

    private void Awake()
    {
        Instance = this;
        InitializeHardwareClock();

        if (hudVisibilityOverriddenByMenu)
        {
            showHUD = showTelemetryHUD;
        }
        else
        {
            showTelemetryHUD = showHUD;
        }

        Application.runInBackground = true;
        QualitySettings.vSyncCount = lockVSyncOff ? 0 : 1;
        Application.targetFrameRate = targetFramerate;
        QualitySettings.shadowDistance = maxShadowDistance;
        QualitySettings.maxQueuedFrames = 1;

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        mpb = new MaterialPropertyBlock();
        colorPropertyId = Shader.PropertyToID("_BaseColor");

        appleRenderer = GetComponent<Renderer>();
        if (appleRenderer != null)
        {
            opaqueSharedMat = appleRenderer.sharedMaterial;
            if (!opaqueSharedMat.HasProperty(colorPropertyId))
            {
                colorPropertyId = Shader.PropertyToID("_Color");
            }
        }

        if (opaqueAppleBackup != null && opaqueAppleBackup.GetComponent<Renderer>() != null)
            opaqueSharedMat = opaqueAppleBackup.GetComponent<Renderer>().sharedMaterial;

        if (transparentAppleBackup != null && transparentAppleBackup.GetComponent<Renderer>() != null)
            transparentSharedMat = transparentAppleBackup.GetComponent<Renderer>().sharedMaterial;

        handBasePositions = new Vector3[]
        {
            Vector3.zero,
            new Vector3(-314.55f, -25.958f, 252.379f), // Index 1: Left Hand
            new Vector3(-313.59f, -25.958f, 251.26f)   // Index 2: Right Hand
        };

        var h1 = FindObjectOfType<hand1_script>();
        var h2 = FindObjectOfType<hand2_script>();

        if (leftHandAnimator == null && h1 != null) leftHandAnimator = h1.animator;
        if (rightHandAnimator == null && h2 != null) rightHandAnimator = h2.animator;

        handAnimators = new Animator[] { null, leftHandAnimator, rightHandAnimator };
        handCaches = new DynamicHandScript[] { null, new Hand1Adapter(h1), new Hand2Adapter(h2) };

        openHandActions = new Action[]
        {
            () => {},
            () => { handCaches[1]?.Open(); handAnimators[1]?.SetBool("CloseHand", false); },
            () => { handCaches[2]?.Open(); handAnimators[2]?.SetBool("CloseHand", false); }
        };

        closeHandActions = new Action[]
        {
            () => {},
            () => { handCaches[1]?.Close(); handAnimators[1]?.SetBool("CloseHand", true); },
            () => { handCaches[2]?.Close(); handAnimators[2]?.SetBool("CloseHand", true); }
        };

        errorFadeActions = new Action[]
        {
            () => {},
            () => {
                if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
                fadeCoroutine = StartCoroutine(ZeroGCFadeAlpha(1.0f, 0.0f, fadeDuration));
            }
        };

        // Initialize O(1) rising-edge dispatch table indexed by bit position (0..31)
        edgeDispatchTable = new Action<uint>[32];
        for (int i = 0; i < 32; i++) edgeDispatchTable[i] = (_) => { };
        edgeDispatchTable[1] = OnTrialTriggeredEdge; // Bit 1: MASK_TRIAL_TRIGGERED
        edgeDispatchTable[4] = OnFallingStartEdge;   // Bit 4: MASK_FALLING
        edgeDispatchTable[6] = OnTriggerFiredEdge;   // Bit 6: MASK_TRIGGER_FIRED
        edgeDispatchTable[7] = OnContactDoneEdge;    // Bit 7: MASK_CONTACT_DONE

        InitializeSharedMemory();
        InjectEarlyUpdateLoop();
    }

    private void Start()
    {
        transform.rotation = Quaternion.Euler(270f, 53.292f, 0f);

        positionFactor = 1;
        Vector3 basePos = handBasePositions[1];
        startYPosition = basePos.y + dropYOffset;
        transform.position = new Vector3(basePos.x, startYPosition, basePos.z);

        ResetAppleMaterial();

        openHandActions[1]();
        openHandActions[2]();

        shouldMove = false;
        resetApple = false;
        resettedApple = true;

        PrebakeDeterministicTrajectorySequence(transform.position, basePos);
        PublishSessionConfigToPython();

        Volatile.Write(ref sharedStateMask, (int)MASK_CONFIG_READY);
        lastObservedEarlyMask = MASK_CONFIG_READY;
        hudDirty = true;

        StartSdaeMasterClockThread();
    }

    private void Update()
    {
        uint currentMask = (uint)Volatile.Read(ref sharedStateMask);
        bool isBusy = (currentMask & (MASK_TRIAL_TRIGGERED | MASK_PREPARING_HANDS | MASK_FALLING | MASK_BENCHMARK_R)) != 0;

        // Key R: Trigger randomized stress benchmark
        if (Input.GetKeyDown(KeyCode.R) && !isBusy)
        {
            StartRandomizedBenchmarkSuite();
        }

        if ((currentMask & MASK_FALLING) != 0 && Time.unscaledDeltaTime > 0f)
        {
            trialFpsAccumulator += 1.0f / Time.unscaledDeltaTime;
            trialFpsFrameCount++;
            Volatile.Write(ref sharedMeanTrialFps, trialFpsAccumulator / trialFpsFrameCount);
        }
    }

    // =========================================================================
    // NATIVE QPC HARDWARE TIMESTAMPING (IDENTICAL EPOCH TO PYTHON)
    // =========================================================================

    private static void InitializeHardwareClock()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (QueryPerformanceFrequency(out long freq) && freq > 0L)
        {
            qpcFrequency = freq;
            useNativeQpc = true;
            return;
        }
#endif
        qpcFrequency = Stopwatch.Frequency;
        useNativeQpc = false;
    }

    /// <summary>
    /// Reads the raw hardware QueryPerformanceCounter and converts ticks to nanoseconds
    /// without floating-point precision loss, matching Python's time.perf_counter_ns().
    /// </summary>
    private static long GetHardwareTimestampNs()
    {
        long ticks;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (useNativeQpc)
        {
            QueryPerformanceCounter(out ticks);
        }
        else
        {
            ticks = Stopwatch.GetTimestamp();
        }
#else
        ticks = Stopwatch.GetTimestamp();
#endif
        long seconds = ticks / qpcFrequency;
        long remainder = ticks % qpcFrequency;
        return (seconds * 1_000_000_000L) + ((remainder * 1_000_000_000L) / qpcFrequency);
    }

    // =========================================================================
    // LAYER 1: DEDICATED 1 kHz MASTER CLOCK WORKER (LOCK-FREE & PHASE-LOCKED)
    // =========================================================================

    private void StartSdaeMasterClockThread()
    {
        sdaeThreadRunning = true;
        sdaeClockThread = new Thread(SdaeMasterClockWorker)
        {
            Name = "SDAE_1kHz_MasterClock",
            IsBackground = true,
            Priority = System.Threading.ThreadPriority.Highest
        };
        sdaeClockThread.Start();
    }

    private void SdaeMasterClockWorker()
    {
        int lastHandledTrialId = 0;
        long fallStartNs = 0L;
        long targetEndNs = 0L;
        int localStepIndex = 0;
        int localTotalSteps = 3000;
        int localTriggerStep = 1500;
        bool localTriggerFired = false;

        bool localDecisionArrived = false;
        int localBufferedDecision = 0;
        float localDecisionTransportMs = 0f;
        float localAnticipationMarginMs = 0f;

        long hzMeasureStartNs = GetHardwareTimestampNs();
        int hzTickCounter = 0;

        while (sdaeThreadRunning)
        {
            long loopStartNs = GetHardwareTimestampNs();
            uint mask = (uint)Volatile.Read(ref sharedStateMask);
            uint benchBit = mask & MASK_BENCHMARK_R;

            // 1. Lock-Free IPC Trigger Polling (Python writes timestamp at Offset 24 before trialId at Offset 0)
            bool isIdle = (mask & (MASK_TRIAL_TRIGGERED | MASK_PREPARING_HANDS | MASK_HANDS_OPEN | MASK_FALLING)) == 0;
            if (isIdle && accessor != null)
            {
                int incomingTrialId = accessor.ReadInt32(0);
                if (incomingTrialId > lastHandledTrialId && incomingTrialId > 0)
                {
                    long detectNs = GetHardwareTimestampNs();
                    Thread.MemoryBarrier();

                    int targetHand = accessor.ReadInt32(4);
                    long pythonTimestampNs = accessor.ReadInt64(24);
                    accessor.Write(16, 1); // unityState = 1 (Preparing Hands)

                    float ipcLatMs = (pythonTimestampNs > 0L && detectNs > pythonTimestampNs)
                        ? (float)((detectNs - pythonTimestampNs) / 1_000_000.0)
                        : 0.0001f;

                    Volatile.Write(ref atomicIpcTriggerLatencyMs, ipcLatMs);
                    lastHandledTrialId = incomingTrialId;

                    int validHand = ((uint)(targetHand - 1) < 2u) ? targetHand : 1;
                    Volatile.Write(ref sharedTargetHand, validHand);
                    Volatile.Write(ref sharedDecision, 0);

                    uint nextMask = NEXT_MASK_LUT[1] | benchBit;
                    Volatile.Write(ref sharedStateMask, (int)nextMask);
                    mask = nextMask;
                }
                else if (incomingTrialId == 0 && lastHandledTrialId != 0)
                {
                    lastHandledTrialId = 0;
                }
            }

            // 2. Trial Fall Initiation (REQ_START_FALL == 0x0B)
            if ((mask & REQ_START_FALL) == REQ_START_FALL)
            {
                localTotalSteps = Volatile.Read(ref sharedTotalSteps);
                localTriggerStep = Volatile.Read(ref sharedTriggerStep);
                localStepIndex = 0;
                localTriggerFired = false;

                localDecisionArrived = false;
                localBufferedDecision = 0;
                localDecisionTransportMs = 0f;
                localAnticipationMarginMs = 0f;

                Volatile.Write(ref sharedStepIndex, 0);
                Volatile.Write(ref sharedDecision, 0);
                Volatile.Write(ref sharedMaxFrameBurst, 0);
                Volatile.Write(ref sharedMeanTrialFps, 0f);
                Volatile.Write(ref atomicDecisionTransportMs, 0f);
                Volatile.Write(ref atomicAnticipationMarginMs, 0f);

                fallStartNs = GetHardwareTimestampNs();
                long theoreticalDurationNs = (long)localTotalSteps * 1_000_000L;
                targetEndNs = fallStartNs + theoreticalDurationNs;

                if (accessor != null && accessor.CanWrite)
                {
                    accessor.Write(20, 0);
                    accessor.Write(80, 0L);
                    Thread.MemoryBarrier();
                    accessor.Write(16, 2); // unityState = 2 (Falling Active)
                }

                uint nextMask = NEXT_MASK_LUT[2] | benchBit;
                Volatile.Write(ref sharedStateMask, (int)nextMask);
                mask = nextMask;
            }

            // 3. Deterministic 1 kHz Trajectory Stepping + Sub-Millisecond Phase Alignment
            if ((mask & MASK_FALLING) != 0)
            {
                // Lock-free detection of classifier prediction (Python writes Offset 80 before Offset 20)
                if (!localDecisionArrived && accessor != null)
                {
                    int rawDec = accessor.ReadInt32(20);
                    if (rawDec == 1 || rawDec == 2)
                    {
                        long arrNs = GetHardwareTimestampNs();
                        Thread.MemoryBarrier();
                        long pyDecTsNs = accessor.ReadInt64(80);

                        localDecisionArrived = true;
                        localBufferedDecision = rawDec;

                        localDecisionTransportMs = (pyDecTsNs > 0L && arrNs > pyDecTsNs)
                            ? (float)((arrNs - pyDecTsNs) / 1_000_000.0)
                            : 0.0001f;

                        double arrivalElapsedMs = (arrNs - fallStartNs) / 1_000_000.0;
                        double theoreticalTriggerMs = (double)localTriggerStep / sdaeTickRateHz * 1000.0;
                        localAnticipationMarginMs = (float)(theoreticalTriggerMs - arrivalElapsedMs);

                        Volatile.Write(ref atomicDecisionTransportMs, localDecisionTransportMs);
                        Volatile.Write(ref atomicAnticipationMarginMs, localAnticipationMarginMs);
                    }
                }

                long nowNs = GetHardwareTimestampNs();
                double elapsedMs = (nowNs - fallStartNs) / 1_000_000.0;
                int targetStep = (int)((elapsedMs / 1000.0) * sdaeTickRateHz);
                if (targetStep > localTotalSteps) targetStep = localTotalSteps;

                // Fine phase-lock spin during the final millisecond to eliminate 0.5 ms sampling bias
                if (targetStep >= localTotalSteps - 1 && nowNs < targetEndNs)
                {
                    while (GetHardwareTimestampNs() < targetEndNs)
                    {
                        Thread.SpinWait(10);
                    }
                    targetStep = localTotalSteps;
                }

                if (targetStep > localStepIndex)
                {
                    localStepIndex = targetStep;
                    Volatile.Write(ref sharedStepIndex, localStepIndex);

                    // Branchless sign-bit check: 1 when localStepIndex >= localTriggerStep
                    uint reachedTriggerBit = ((uint)(localTriggerStep - localStepIndex - 1)) >> 31;
                    if (reachedTriggerBit == 1u && !localTriggerFired)
                    {
                        localTriggerFired = true;

                        int finalDecision;
                        if (localDecisionArrived)
                        {
                            finalDecision = localBufferedDecision;
                        }
                        else
                        {
                            double theoreticalTriggerMs = (double)localTriggerStep / sdaeTickRateHz * 1000.0;
                            localAnticipationMarginMs = (float)(theoreticalTriggerMs - elapsedMs);
                            Volatile.Write(ref atomicAnticipationMarginMs, localAnticipationMarginMs);

                            // In TRAINING mode, fall back to ground truth for guided calibration.
                            // In TESTING mode, assign 0 (NONE) so late/missing decisions keep the hand open
                            // and trigger the apple error fade-out at contact.
                            finalDecision = trainingMode ? Volatile.Read(ref sharedTargetHand) : 0;
                        }

                        Volatile.Write(ref sharedDecision, finalDecision);

                        uint nextMask = NEXT_MASK_LUT[3] | benchBit;
                        Volatile.Write(ref sharedStateMask, (int)nextMask);
                        mask = nextMask;
                    }

                    // Trajectory completion: Write Unity telemetry to Offsets 56..88 BEFORE unityState = 3
                    uint reachedEndBit = ((uint)(localTotalSteps - localStepIndex - 1)) >> 31;
                    if (reachedEndBit == 1u)
                    {
                        long endNs = GetHardwareTimestampNs();
                        double actualMs = (endNs - fallStartNs) / 1_000_000.0;
                        double theoreticalTotalMs = (double)localTotalSteps / sdaeTickRateHz * 1000.0;
                        float clockJitter = (float)Math.Abs(actualMs - theoreticalTotalMs);

                        Volatile.Write(ref atomicActualKinematicMs, (float)actualMs);
                        Volatile.Write(ref atomicClockJitterMs, clockJitter);

                        float ipcTrigLat = Volatile.Read(ref atomicIpcTriggerLatencyMs);
                        float decTransLat = Volatile.Read(ref atomicDecisionTransportMs);
                        float antMargin = Volatile.Read(ref atomicAnticipationMarginMs);
                        int maxBurst = Volatile.Read(ref sharedMaxFrameBurst);
                        float meanFps = Volatile.Read(ref sharedMeanTrialFps);

                        if (accessor != null && accessor.CanWrite)
                        {
                            accessor.Write(56, ipcTrigLat);
                            accessor.Write(60, decTransLat);
                            accessor.Write(64, (float)actualMs);
                            accessor.Write(68, clockJitter);
                            accessor.Write(72, maxBurst);
                            accessor.Write(76, meanFps);
                            accessor.Write(88, antMargin);

                            Thread.MemoryBarrier();
                            accessor.Write(16, 3); // unityState = 3 (Trial Done & Telemetry Ready)
                        }

                        uint nextMask = NEXT_MASK_LUT[4] | benchBit;
                        Volatile.Write(ref sharedStateMask, (int)nextMask);
                    }
                }
            }

            hzTickCounter++;
            long hzNowNs = GetHardwareTimestampNs();
            if ((hzNowNs - hzMeasureStartNs) >= 1_000_000_000L)
            {
                Volatile.Write(ref atomicClockHz, hzTickCounter);
                hzTickCounter = 0;
                hzMeasureStartNs = hzNowNs;
            }

            // Maintain strict 1.00 ms (1000 Hz) pacing
            if ((GetHardwareTimestampNs() - loopStartNs) < 650_000L)
            {
                Thread.Sleep(0);
            }
            while ((GetHardwareTimestampNs() - loopStartNs) < 1_000_000L)
            {
                Thread.SpinWait(20);
            }
        }
    }

    // =========================================================================
    // LAYER 2: PLAYERLOOP EARLYUPDATE INJECTION (O(1) DE BRUIJN DISPATCH)
    // =========================================================================

    private void InjectEarlyUpdateLoop()
    {
        PlayerLoopSystem currentLoop = PlayerLoop.GetCurrentPlayerLoop();
        for (int i = 0; i < currentLoop.subSystemList.Length; i++)
        {
            if (currentLoop.subSystemList[i].type == typeof(EarlyUpdate))
            {
                var oldSub = currentLoop.subSystemList[i].subSystemList;
                var newSub = new PlayerLoopSystem[oldSub.Length + 1];
                newSub[0] = new PlayerLoopSystem
                {
                    type = typeof(SdaeEarlyUpdateSystem),
                    updateDelegate = OnSdaeEarlyUpdate
                };
                Array.Copy(oldSub, 0, newSub, 1, oldSub.Length);
                currentLoop.subSystemList[i].subSystemList = newSub;
                break;
            }
        }
        PlayerLoop.SetPlayerLoop(currentLoop);
    }

    private void RemoveEarlyUpdateLoop()
    {
        PlayerLoopSystem currentLoop = PlayerLoop.GetCurrentPlayerLoop();
        for (int i = 0; i < currentLoop.subSystemList.Length; i++)
        {
            if (currentLoop.subSystemList[i].type == typeof(EarlyUpdate))
            {
                var oldSub = currentLoop.subSystemList[i].subSystemList;
                int keepCount = 0;
                for (int j = 0; j < oldSub.Length; j++)
                {
                    if (oldSub[j].type != typeof(SdaeEarlyUpdateSystem)) keepCount++;
                }

                var newSub = new PlayerLoopSystem[keepCount];
                int idx = 0;
                for (int j = 0; j < oldSub.Length; j++)
                {
                    if (oldSub[j].type != typeof(SdaeEarlyUpdateSystem))
                        newSub[idx++] = oldSub[j];
                }
                currentLoop.subSystemList[i].subSystemList = newSub;
                break;
            }
        }
        PlayerLoop.SetPlayerLoop(currentLoop);
    }

    private void OnSdaeEarlyUpdate()
    {
        if (!Application.isPlaying || this == null) return;

        uint currentMask = (uint)Volatile.Read(ref sharedStateMask);
        uint pendingEdges = currentMask & ~lastObservedEarlyMask;
        lastObservedEarlyMask = currentMask;

        // O(1) lowest-set-bit isolation via De Bruijn multiplication
        while (pendingEdges != 0)
        {
            uint lsb = pendingEdges & (uint)-(int)pendingEdges;
            int bitIndex = DE_BRUIJN_BIT_POS[(lsb * 0x077CB531u) >> 27];
            edgeDispatchTable[bitIndex](currentMask);
            pendingEdges ^= lsb;
        }

        // Active level: MASK_PREPARING_HANDS (Zero-GC hand opening verification)
        if ((currentMask & MASK_PREPARING_HANDS) != 0)
        {
            handPrepFrameCounter++;
            handPrepElapsedSec += Time.unscaledDeltaTime;

            if (handPrepFrameCounter >= 2 && ((IsHandOpen(1) && IsHandOpen(2)) || handPrepElapsedSec >= maxOpenWaitTimeout))
            {
                SwapMaskBitsAtomic(MASK_HANDS_OPEN, MASK_PREPARING_HANDS);
            }
        }

        // Active level: MASK_FALLING (Apply prebaked 3D trajectory coordinates)
        if ((currentMask & MASK_FALLING) != 0)
        {
            int step = Volatile.Read(ref sharedStepIndex);
            int delta = step - lastRenderedStep;
            lastFrameStepDelta = delta;
            if (delta > maxStepsInSingleFrame)
            {
                maxStepsInSingleFrame = delta;
                Volatile.Write(ref sharedMaxFrameBurst, maxStepsInSingleFrame);
            }
            lastRenderedStep = step;

            transform.position = prebakedTrajectory[step];
        }
    }

    private void OnTrialTriggeredEdge(uint currentMask)
    {
        trialCounter++;
        positionFactor = Volatile.Read(ref sharedTargetHand);

        if ((currentMask & MASK_BENCHMARK_R) != 0)
        {
            speedPercentage = UnityEngine.Random.Range(Mathf.RoundToInt(minRandomSpeedPct), Mathf.RoundToInt(maxRandomSpeedPct) + 1);
            decisionPercentile = UnityEngine.Random.Range(Mathf.RoundToInt(minRandomPercentile), Mathf.RoundToInt(maxRandomPercentile) + 1);
            benchmarkStatusBanner = $"BENCHMARK [R] ({benchmarkCompletedTrials + 1}/{benchmarkTrialCount}) | Speed: {speedPercentage:F0}% | Trigger: {decisionPercentile:F0}%";
        }

        // Re-enable and restore opaque apple appearance at the start of every trial
        ResetAppleMaterial();

        Vector3 basePos = handBasePositions[positionFactor];
        startYPosition = basePos.y + dropYOffset;
        transform.position = new Vector3(basePos.x, startYPosition, basePos.z);
        transform.rotation = Quaternion.Euler(270f, 53.292f, 0f);

        int steps = PrebakeDeterministicTrajectorySequence(transform.position, basePos);
        int triggerStep = Mathf.Clamp(
            Mathf.RoundToInt(steps * (Mathf.Clamp(decisionPercentile, 10f, 95f) / 100f)),
            25,
            steps - 1
        );

        Volatile.Write(ref sharedTotalSteps, steps);
        Volatile.Write(ref sharedTriggerStep, triggerStep);

        if (accessor != null && accessor.CanWrite)
        {
            accessor.Write(8, steps);
            accessor.Write(12, Mathf.Max(5, triggerStep - 20));
            accessor.Write(40, speedPercentage);
            accessor.Write(44, decisionPercentile);
        }

        openHandActions[1]();
        openHandActions[2]();

        handPrepFrameCounter = 0;
        handPrepElapsedSec = 0f;
        SetMaskBitsAtomic(MASK_PREPARING_HANDS);
        hudDirty = true;
    }

    private void OnFallingStartEdge(uint currentMask)
    {
        shouldMove = true;
        resettedApple = false;
        lastRenderedStep = 0;
        maxStepsInSingleFrame = 0;
        trialFpsAccumulator = 0f;
        trialFpsFrameCount = 0;
        hudDirty = true;
    }

    private void OnTriggerFiredEdge(uint currentMask)
    {
        int pred = Volatile.Read(ref sharedDecision);
        closeHandActions[pred]();
        hudDirty = true;
    }

    private bool IsHandOpen(int handIndex)
    {
        var cache = handCaches[handIndex];
        if (cache != null && cache.IsFullyOpen) return true;

        var anim = handAnimators[handIndex];
        if (anim != null)
        {
            bool isCloseBoolActive = anim.GetBool("CloseHand");
            AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
            string openName = cache?.OpenStateName ?? "Open";
            return (!isCloseBoolActive && info.IsName(openName) && info.normalizedTime >= 0.90f && !anim.IsInTransition(0));
        }
        return true;
    }

    private void OnContactDoneEdge(uint currentMask)
    {
        shouldMove = false;
        resettedApple = true;

        int finalSteps = Volatile.Read(ref sharedTotalSteps);
        transform.position = prebakedTrajectory[finalSteps];

        float ipcTriggerLat = Volatile.Read(ref atomicIpcTriggerLatencyMs);
        lastClockJitterMs = Volatile.Read(ref atomicClockJitterMs);

        int finalDecisionInt = Volatile.Read(ref sharedDecision);
        bool isCorrect = (finalDecisionInt == positionFactor);
        correct = isCorrect ? 1 : 0;

        string predictionLabel = (finalDecisionInt == 1) ? "LEFT" : (finalDecisionInt == 2) ? "RIGHT" : "TIMEOUT";
        string gtLabel = (positionFactor == 1) ? "LEFT" : "RIGHT";

        // In TESTING mode (!trainingMode), fade out the apple whenever the prediction is wrong or late
        int fadeDispatchIdx = (!isCorrect && fadeOnError && !trainingMode) ? 1 : 0;
        errorFadeActions[fadeDispatchIdx]();

        if ((currentMask & MASK_BENCHMARK_R) != 0)
        {
            benchmarkCompletedTrials++;
            if (benchmarkCompletedTrials >= benchmarkTrialCount)
            {
                ClearMaskBitsAtomic(MASK_BENCHMARK_R);
                if (accessor != null && accessor.CanWrite) accessor.Write(52, 0);
                benchmarkStatusBanner = "BENCHMARK [R] COMPLETED -> Plots exported & displayed by Python";
            }
        }

        RecordTrialMetrics(lastClockJitterMs, ipcTriggerLat, isCorrect, predictionLabel, gtLabel);

        ClearMaskBitsAtomic(~CLEAR_TRIAL_BITS);
        lastObservedEarlyMask = (uint)Volatile.Read(ref sharedStateMask);
        hudDirty = true;
    }

    // =========================================================================
    // ATOMIC LOCK-FREE BITMASK OPERATIONS (CAS)
    // =========================================================================

    private void SetMaskBitsAtomic(uint bitsToSet)
    {
        int initial, computed;
        do
        {
            initial = Volatile.Read(ref sharedStateMask);
            computed = (int)((uint)initial | bitsToSet);
        }
        while (Interlocked.CompareExchange(ref sharedStateMask, computed, initial) != initial);
    }

    private void ClearMaskBitsAtomic(uint bitsToClear)
    {
        int initial, computed;
        do
        {
            initial = Volatile.Read(ref sharedStateMask);
            computed = (int)((uint)initial & ~bitsToClear);
        }
        while (Interlocked.CompareExchange(ref sharedStateMask, computed, initial) != initial);
    }

    private void SwapMaskBitsAtomic(uint bitsToSet, uint bitsToClear)
    {
        int initial, computed;
        do
        {
            initial = Volatile.Read(ref sharedStateMask);
            computed = (int)(((uint)initial & ~bitsToClear) | bitsToSet);
        }
        while (Interlocked.CompareExchange(ref sharedStateMask, computed, initial) != initial);
    }

    // =========================================================================
    // KINEMATIC TRAJECTORY PRE-BAKE & BENCHMARK R
    // =========================================================================

    private int PrebakeDeterministicTrajectorySequence(Vector3 spawnPos, Vector3 fallbackTarget)
    {
        calculatedTargetY = fallbackTarget.y;

        if (Physics.Raycast(spawnPos, Vector3.down, out RaycastHit hit, 20.0f))
        {
            if (hit.collider.gameObject.name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                calculatedTargetY = hit.point.y;
            }
        }

        float safeSpeedPct = Mathf.Clamp(speedPercentage, 10f, 300f);
        float stepScaleFactor = 100f / safeSpeedPct;
        int baseSteps = Mathf.RoundToInt(targetTrialDurationSec * sdaeTickRateHz);
        int steps = Mathf.Min(MAX_STEPS - 2, Mathf.RoundToInt(baseSteps * stepScaleFactor));

        float stepIncrementY = (startYPosition - calculatedTargetY) / steps;
        float fixedX = spawnPos.x;
        float fixedZ = spawnPos.z;

        for (int i = 0; i < steps; i++)
        {
            prebakedTrajectory[i] = new Vector3(fixedX, startYPosition - (stepIncrementY * i), fixedZ);
        }
        prebakedTrajectory[steps] = new Vector3(fixedX, calculatedTargetY, fixedZ);

        return steps;
    }

    private void StartRandomizedBenchmarkSuite()
    {
        benchmarkCompletedTrials = 0;
        benchmarkStatusBanner = $"BENCHMARK [R] ACTIVE (0/{benchmarkTrialCount}) -> Waiting for Python trigger...";
        hudDirty = true;

        SetMaskBitsAtomic(MASK_BENCHMARK_R);

        if (accessor != null && accessor.CanWrite)
        {
            accessor.Write(0, 0);
            Thread.MemoryBarrier();
            accessor.Write(52, 1);
        }
    }

    private void PublishSessionConfigToPython()
    {
        int initSteps = Mathf.Min(MAX_STEPS - 2, Mathf.RoundToInt(targetTrialDurationSec * sdaeTickRateHz * (100f / Mathf.Clamp(speedPercentage, 10f, 300f))));
        int initTrigger = Mathf.Clamp(Mathf.RoundToInt(initSteps * (Mathf.Clamp(decisionPercentile, 10f, 95f) / 100f)), 25, initSteps - 1);

        Volatile.Write(ref sharedTotalSteps, initSteps);
        Volatile.Write(ref sharedTriggerStep, initTrigger);

        if (accessor != null && accessor.CanWrite)
        {
            accessor.Write(0, 0);
            accessor.Write(8, initSteps);
            accessor.Write(12, Mathf.Max(5, initTrigger - 20));
            accessor.Write(16, 0);
            accessor.Write(20, 0);
            accessor.Write(32, Mathf.Max(1, subjectNumber));
            accessor.Write(36, trainingMode ? 1 : 0);
            accessor.Write(40, Mathf.Clamp(speedPercentage, 10f, 300f));
            accessor.Write(44, Mathf.Clamp(decisionPercentile, 10f, 95f));
            accessor.Write(52, 0);
            Thread.MemoryBarrier();
            accessor.Write(48, 1);
        }
    }

    #region Material Property Block - Zero-GC Visual Feedback

    private IEnumerator ZeroGCFadeAlpha(float startAlpha, float targetAlpha, float duration)
    {
        if (appleRenderer == null) yield break;

        if (transparentSharedMat != null) appleRenderer.sharedMaterial = transparentSharedMat;

        appleRenderer.GetPropertyBlock(mpb);
        Color baseColor = mpb.GetColor(colorPropertyId);
        if (baseColor == Color.clear) baseColor = Color.white;

        mpb.SetColor(colorPropertyId, new Color(baseColor.r, baseColor.g, baseColor.b, startAlpha));
        appleRenderer.SetPropertyBlock(mpb);

        float elapsedTime = 0f;

        while (elapsedTime < duration)
        {
            elapsedTime += Time.unscaledDeltaTime;
            float currentAlpha = Mathf.Lerp(startAlpha, targetAlpha, elapsedTime / duration);

            mpb.SetColor(colorPropertyId, new Color(baseColor.r, baseColor.g, baseColor.b, currentAlpha));
            appleRenderer.SetPropertyBlock(mpb);

            yield return null;
        }

        if (targetAlpha <= 0.05f) appleRenderer.enabled = false;
    }

    private void ResetAppleMaterial()
    {
        if (fadeCoroutine != null) { StopCoroutine(fadeCoroutine); fadeCoroutine = null; }

        if (appleRenderer != null)
        {
            if (opaqueSharedMat != null) appleRenderer.sharedMaterial = opaqueSharedMat;

            appleRenderer.GetPropertyBlock(mpb);
            Color baseColor = mpb.GetColor(colorPropertyId);
            if (baseColor == Color.clear) baseColor = Color.white;

            mpb.SetColor(colorPropertyId, new Color(baseColor.r, baseColor.g, baseColor.b, 1.0f));
            appleRenderer.SetPropertyBlock(mpb);

            appleRenderer.enabled = true;
        }
    }

    #endregion

    #region RAM & Lifecycle Cleanup

    private void InitializeSharedMemory()
    {
        try
        {
            mmf = MemoryMappedFile.CreateOrOpen("sdae_bci_memory", PAYLOAD_SIZE, MemoryMappedFileAccess.ReadWrite);
            accessor = mmf.CreateViewAccessor(0, PAYLOAD_SIZE, MemoryMappedFileAccess.ReadWrite);
        }
        catch (Exception ex)
        {
#if UNITY_EDITOR
            UnityEngine.Debug.LogError($"[RAM IPC Error] {ex.Message}");
#endif
        }
    }

    private void OnDestroy()
    {
        sdaeThreadRunning = false;
        if (sdaeClockThread != null && sdaeClockThread.IsAlive)
        {
            sdaeClockThread.Join(100);
        }

        RemoveEarlyUpdateLoop();

        if (accessor != null && accessor.CanWrite)
        {
            accessor.Write(48, 0);
            accessor.Write(52, 0);
            accessor.Write(16, 0);
        }

        accessor?.Dispose();
        mmf?.Dispose();
    }

    #endregion

    #region Telemetry & Strict Zero-GC English HUD

    private void RecordTrialMetrics(float clockJitterMs, float ipcTriggerLatencyMs, bool isCorrect, string prediction, string groundTruth)
    {
        if (isCorrect) correctTrials++;

        int index = (trialCounter - 1) % MAX_TELEMETRY_LOGS;
        jitterBuffer[index] = clockJitterMs;
        ipcLatencyBuffer[index] = ipcTriggerLatencyMs;

        int validCount = Mathf.Min(trialCounter, MAX_TELEMETRY_LOGS);
        float sumJitter = 0f;
        float sumIpc = 0f;
        maxIpcLatencyMs = 0f;

        for (int i = 0; i < validCount; i++)
        {
            sumJitter += jitterBuffer[i];
            float ipcVal = ipcLatencyBuffer[i];
            sumIpc += ipcVal;
            if (ipcVal > maxIpcLatencyMs) maxIpcLatencyMs = ipcVal;
        }

        avgClockJitterMs = sumJitter / validCount;
        avgIpcLatencyMs = sumIpc / validCount;

        float sumOfSquares = 0f;
        for (int i = 0; i < validCount; i++)
        {
            float diff = jitterBuffer[i] - avgClockJitterMs;
            sumOfSquares += diff * diff;
        }
        stdDevClockJitterMs = Mathf.Sqrt(sumOfSquares / validCount);

        string statusTag = isCorrect ? "OK" : "ERR";
        logHistoryBuffer[logHistoryIndex] = $"Trial #{trialCounter} | [{statusTag}] IPC: {ipcTriggerLatencyMs * 1000f:F1} µs | Spd: {speedPercentage:F0}% Trig: {decisionPercentile:F0}% | {prediction}/{groundTruth}";
        logHistoryIndex = (logHistoryIndex + 1) % 5;
        hudDirty = true;
    }

    private void InitializeGUIStyles()
    {
        headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.green } };
        modeStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.8f, 0.2f) } };
        benchmarkStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.4f, 0.8f) } };
        metricStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, normal = { textColor = Color.white } };
        standbyStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Bold, normal = { textColor = Color.cyan } };
        logStyle = new GUIStyle(GUI.skin.label) { fontSize = 9, normal = { textColor = Color.yellow } };
        guiStylesInitialized = true;
    }

    /// <summary>
    /// Rebuilds cached HUD strings only when state changes or the throttle interval elapses,
    /// guaranteeing 0 bytes of heap allocation inside OnGUI() across Layout/Repaint passes.
    /// </summary>
    private void RefreshCachedHUDText(uint mask)
    {
        int curStep = Volatile.Read(ref sharedStepIndex);
        int totSteps = Volatile.Read(ref sharedTotalSteps);
        int trigStep = Volatile.Read(ref sharedTriggerStep);
        float ipcTrigLat = Volatile.Read(ref atomicIpcTriggerLatencyMs);
        float decTransLat = Volatile.Read(ref atomicDecisionTransportMs);
        float antMargin = Volatile.Read(ref atomicAnticipationMarginMs);
        int clockHz = Volatile.Read(ref atomicClockHz);

        float accuracyPct = (trialCounter > 0) ? ((float)correctTrials / trialCounter) * 100f : 0f;
        bool isSyntheticSession = trainingMode || ((mask & MASK_BENCHMARK_R) != 0);
        string sessionModeStr = trainingMode ? "TRAINING (CALIBRATION)" : "TESTING (ONLINE BCI)";
        string accuracyLabelStr = isSyntheticSession ? "Rig Sync Accuracy" : "BCI Classifier Accuracy";

        cachedSubjectLine = $"<b>SUBJECT:</b> #{subjectNumber} | <b>MODE:</b> {sessionModeStr} | <b>Speed:</b> {speedPercentage:F0}% | <b>Trigger:</b> {decisionPercentile:F0}%";
        cachedMaskLine = $"<b>Active Mask:</b> 0x{mask:X3} (Bin: {Convert.ToString(mask, 2).PadLeft(9, '0')}) | <b>SDAE Clock:</b> {clockHz} Hz";
        cachedAccuracyLine = $"<b>Total Trials:</b> {trialCounter} | <b>{accuracyLabelStr}:</b> {accuracyPct:F1}%";
        cachedIpcLine = $"<b>IPC Trigger Latency:</b> {ipcTrigLat * 1000f:F1} µs (Mean: {avgIpcLatencyMs * 1000f:F1} µs | Max: {maxIpcLatencyMs * 1000f:F1} µs)";
        cachedTransportLine = $"<b>Decision Transport (Step {trigStep}):</b> {decTransLat * 1000f:F1} µs | <b>Anticipation Margin:</b> {antMargin:+0.00;-0.00;0.00} ms";
        cachedJitterLine = $"<b>1 kHz Clock Error:</b> {avgClockJitterMs:F3} ms (σ: ±{stdDevClockJitterMs:F3} ms) | <b>Last:</b> ±{lastClockJitterMs:F3} ms";
        cachedGpuLine = $"<b>GPU FPS:</b> {(1.0f / Time.smoothDeltaTime):F0} | <b>1 kHz Step:</b> {curStep} / {totSteps} | <b>Render Burst:</b> {lastFrameStepDelta} steps";
    }

    private void OnGUI()
    {
        if (!showHUD || !showTelemetryHUD) return;
        if (!guiStylesInitialized) InitializeGUIStyles();

        if (hudDirty || Time.unscaledTime >= nextHudRefreshTime)
        {
            uint mask = (uint)Volatile.Read(ref sharedStateMask);
            RefreshCachedHUDText(mask);
            hudDirty = false;
            nextHudRefreshTime = Time.unscaledTime + Mathf.Max(0.05f, hudRefreshIntervalSec);
        }

        GUI.Box(new Rect(10, 10, 565, 385), "");
        GUILayout.BeginArea(new Rect(20, 15, 545, 375));

        GUILayout.Label("SDAE 100% LOCK-FREE BITMASK ENGINE - 1 kHz + EARLYUPDATE", headerStyle);
        GUILayout.Label(cachedSubjectLine, modeStyle);
        GUILayout.Label(cachedMaskLine, standbyStyle);

        if (!string.IsNullOrEmpty(benchmarkStatusBanner))
        {
            GUILayout.Label(benchmarkStatusBanner, benchmarkStyle);
        }

        GUILayout.Label(cachedAccuracyLine, metricStyle);
        GUILayout.Label(cachedIpcLine, metricStyle);
        GUILayout.Label(cachedTransportLine, metricStyle);
        GUILayout.Label(cachedJitterLine, metricStyle);

        GUILayout.Space(3);
        GUILayout.Label(cachedGpuLine, metricStyle);

        GUILayout.Space(5);
        GUILayout.Label("<b>REAL-TIME COMMUNICATION TELEMETRY:</b>", headerStyle);
        for (int i = 0; i < 5; i++)
        {
            if (!string.IsNullOrEmpty(logHistoryBuffer[i])) GUILayout.Label(logHistoryBuffer[i], logStyle);
        }
        GUILayout.EndArea();
    }

    #endregion
}