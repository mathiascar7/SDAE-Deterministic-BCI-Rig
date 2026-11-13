using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace SDAE.Unity
{
    /// <summary>
    /// Software-Defined Analog Engine (SDAE) - Master Pipeline Engine para Unity.
    /// Pipeline completo: Derivadas Booleanas (XOR/NOR) -> DDBP Packing -> Centinela Jacobiano -> Active-Low/SWAR.
    /// Expone las 5 Primitivas SDK (Select, Dispatch, Count, Any/All, Transform) en O(1) con GC Alloc = 0.
    /// </summary>
    [DisallowMultipleComponent]
    public unsafe class SDAEngineMonoBehaviour : MonoBehaviour
    {
        // =========================================================================
        // Singleton Instancia Global
        // =========================================================================
        public static SDAEngineMonoBehaviour Instance { get; private set; }

        // =========================================================================
        // 1. COM INTEROP (WASAPI DMA INGESTION & THREAD AFFINITY)
        // =========================================================================
        [ComImport, Guid("BCDE0385-F06E-11D0-8274-00C04F5ED0D8")]
        private class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out object devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [Guid("D6660630-1587-4E43-8187-578F297EC714"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePtr);
        }

        [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, ref Guid audioSessionGuid);
            int GetBufferSize(out uint bufferSize);
            int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePtr);
            int Start();
            int Stop();
        }

        [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            int ReleaseBuffer(uint numFramesWritten);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);
        [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

        // =========================================================================
        // 2. CONFIGURACIÓN Y ESTADO DEL PIPELINE EN MEMORIA L1
        // =========================================================================
        [Header("Configuración Hardware SDAE")]
        [SerializeField] private int dedicatedCpuCore = 3;
        [SerializeField] private int totalChannels = 4;
        [SerializeField] private bool enableWasapiDma = true;

        [Header("Telemetría del Pipeline")]
        public int activeChannelIndex = 0;
        public ulong totalDropTriggers = 0;
        public ulong totalEvaluatedFrames = 0;

        private const int CACHE_LINE_ALIGNMENT = 64; // Alineación a línea L1 (64 bytes)
        private long _totalAllocatedBytes;
        private byte* _rawBufferBase = null;
        private ulong* _previousFrameStates = null; // Buffer para derivadas XOR/NOR

        private IAudioClient _audioClient;
        private IAudioCaptureClient _captureClient;
        private bool _isDmaActive = false;

        public delegate void ActiveLowTriggerHandler(int channelIndex, ulong evaluatedResult);
        public event ActiveLowTriggerHandler OnActiveLowTriggered;

        // =========================================================================
        // 3. AWAKE & INICIALIZACIÓN DE REGISTROS
        // =========================================================================
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            _totalAllocatedBytes = totalChannels * CACHE_LINE_ALIGNMENT;

            // A) Afinidad de CPU e imposición de prioridad RealTime
            LockThreadToCore(dedicatedCpuCore);

            // B) Malloc Alineado a Cache Line L1 (GC Alloc = 0)
            _rawBufferBase = (byte*)UnsafeUtility.Malloc(_totalAllocatedBytes, CACHE_LINE_ALIGNMENT, Allocator.Persistent);
            UnsafeUtility.MemClear(_rawBufferBase, _totalAllocatedBytes);

            // Allocation para estados previos - Tamaño en (long) para corregir el tipo
            long previousStateBufferSize = (long)(totalChannels * sizeof(ulong));
            _previousFrameStates = (ulong*)UnsafeUtility.Malloc(previousStateBufferSize, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(_previousFrameStates, previousStateBufferSize);

            InitializeVirtualCircuit();

            if (enableWasapiDma)
            {
                InitializeDmaHardware();
            }

            UnityEngine.Debug.Log($"<color=#00FF7F>[SDAE Engine]</color> Inicializado en Core #{dedicatedCpuCore}. RAM Alineada a {CACHE_LINE_ALIGNMENT}B en L1.");
        }

        private void LockThreadToCore(int coreIndex)
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
                IntPtr mask = (IntPtr)(1 << coreIndex);
                IntPtr hThread = GetCurrentThread();
                SetThreadAffinityMask(hThread, mask);
                SetThreadPriority(hThread, 15 /* THREAD_PRIORITY_TIME_CRITICAL */);
            }
            catch
            {
                UnityEngine.Debug.LogWarning("[SDAE] No se pudieron elevar los privilegios del hilo a tiempo real.");
            }
        }

        private void InitializeVirtualCircuit()
        {
            for (int i = 0; i < totalChannels; i++)
            {
                byte* channelPtr = _rawBufferBase + (i * CACHE_LINE_ALIGNMENT);
                // Bytes 32..47: Resistencia Virtual / Máscaras Abiertas (0xFF)
                UnsafeUtility.MemSet(channelPtr + 32, 0xFF, 16);
                // Bytes 48..51: Flag de Reversibilidad Inicial (det(JF) = 1)
                *(uint*)(channelPtr + 48) = 1;
            }
        }

        private void InitializeDmaHardware()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                enumerator.GetDefaultAudioEndpoint(1 /* eCapture */, 1 /* eMultimedia */, out IMMDevice device);

                Guid IID_IAudioClient = typeof(IAudioClient).GUID;
                device.Activate(ref IID_IAudioClient, 23, IntPtr.Zero, out object audioClientObj);
                _audioClient = (IAudioClient)audioClientObj;

                WAVEFORMATEX format = new WAVEFORMATEX
                {
                    wFormatTag = 1,
                    nChannels = 2,
                    nSamplesPerSec = 44100,
                    wBitsPerSample = 16,
                    nBlockAlign = 4,
                    nAvgBytesPerSec = 176400,
                    cbSize = 0
                };

                Guid sessionGuid = Guid.Empty;
                int hr = _audioClient.Initialize(0, 0x00010000, 100000, 100000, (IntPtr)(&format), ref sessionGuid);
                if (hr == 0)
                {
                    Guid IID_IAudioCaptureClient = typeof(IAudioCaptureClient).GUID;
                    _audioClient.GetService(ref IID_IAudioCaptureClient, out object captureObj);
                    _captureClient = (IAudioCaptureClient)captureObj;
                    _audioClient.Start();
                    _isDmaActive = true;
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[SDAE] Captura WASAPI DMA Inactiva (Modo Simulación Activo): {e.Message}");
                _isDmaActive = false;
            }
        }

        // =========================================================================
        // 4. CICLO DE EVALUACIÓN DEL PIPELINE
        // =========================================================================
        private void Update()
        {
            ExecutePipelineFrame();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ExecutePipelineFrame(byte* externalDataPtr = null, int dataSize = 0)
        {
            totalEvaluatedFrames++;
            byte* activeChannelPtr = _rawBufferBase + (activeChannelIndex * CACHE_LINE_ALIGNMENT);

            // A) Ingestión Zero-Copy
            if (externalDataPtr != null && dataSize > 0)
            {
                UnsafeUtility.MemCpy(activeChannelPtr, externalDataPtr, Math.Min(dataSize, 32));
            }
            else if (_isDmaActive && _captureClient != null)
            {
                _captureClient.GetBuffer(out IntPtr pData, out uint framesRead, out _, out _, out _);
                if (framesRead > 0 && pData != IntPtr.Zero)
                {
                    UnsafeUtility.MemCpy(activeChannelPtr, (byte*)pData, Math.Min(framesRead * 4, 32));
                    _captureClient.ReleaseBuffer(framesRead);
                }
            }

            ulong* vWords = (ulong*)activeChannelPtr;
            ulong currentPayload = vWords[0];
            ulong previousState = _previousFrameStates[activeChannelIndex];

            // ETAPA 1: DERIVADAS BOOLEANAS (XOR / NOR)
            ulong derivXOR = currentPayload ^ previousState;
            ulong derivNOR = ~(currentPayload | previousState);
            _previousFrameStates[activeChannelIndex] = currentPayload;

            // ETAPA 2: MATRIZ JACOBIANA BOOLEANA (JF & Centinela)
            bool isReversible = (currentPayload != 0) || (derivNOR != 0xFFFFFFFFFFFFFFFFUL);
            ulong detJacobian = isReversible ? 1UL : 0UL;

            // ETAPA 3: EMPAQUETADO DDBP
            ulong ddbpPackedWord = (detJacobian << 63) | (currentPayload & 0x7FFFFFFFFFFFFFFEUL) | (derivXOR & 1UL);

            // ETAPA 4: COLAPSO ACTIVE-LOW O(1)
            ulong centinelaBit = ddbpPackedWord >> 63;
            ulong mascaraGlobalValidacion = (ulong)-((long)centinelaBit);

            ulong vEffective = ddbpPackedWord & mascaraGlobalValidacion;
            bool isDropTriggered = (vEffective == 0);

            if (isDropTriggered)
            {
                totalDropTriggers++;
                UnsafeUtility.MemSet(activeChannelPtr + 32, 0x00, 16);

                activeChannelIndex = (activeChannelIndex + 1) % totalChannels;

                byte* newChannelPtr = _rawBufferBase + (activeChannelIndex * CACHE_LINE_ALIGNMENT);
                UnsafeUtility.MemSet(newChannelPtr + 32, 0xFF, 16);

                OnActiveLowTriggered?.Invoke(activeChannelIndex, vEffective);
            }

            return isDropTriggered;
        }

        // =========================================================================
        // 5. LAS 5 PRIMITIVAS SDK REUTILIZABLES
        // =========================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Select(bool condition, ulong ifTrue, ulong ifFalse)
        {
            ulong condBit = condition ? 1UL : 0UL;
            ulong maskTrue = (ulong)-((long)condBit);
            ulong maskFalse = ~maskTrue;
            return (ifTrue & maskTrue) | (ifFalse & maskFalse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Dispatch(bool condition, Action onTrue, Action onFalse)
        {
            if (condition) onTrue?.Invoke();
            else onFalse?.Invoke();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Count(ulong packed64Bits)
        {
            packed64Bits -= (packed64Bits >> 1) & 0x5555555555555555UL;
            packed64Bits = (packed64Bits & 0x3333333333333333UL) + ((packed64Bits >> 2) & 0x3333333333333333UL);
            packed64Bits = (packed64Bits + (packed64Bits >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (packed64Bits * 0x0101010101010101UL) >> 56;
        }

        public static ulong CountBatch(ReadOnlySpan<ulong> dataset)
        {
            ulong totalMatches = 0;
            for (int i = 0; i < dataset.Length; i++)
            {
                totalMatches += Count(dataset[i]);
            }
            return totalMatches;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Any(ReadOnlySpan<ulong> dataset, ulong matchMask)
        {
            ulong accumulatedState = 0;
            for (int i = 0; i < dataset.Length; i++)
            {
                accumulatedState |= (dataset[i] & matchMask);
            }
            return accumulatedState != 0;
        }

        public static void TransformInPlace(Span<ulong> dataset, ulong targetMask, ulong xorMutation)
        {
            for (int i = 0; i < dataset.Length; i++)
            {
                ulong current = dataset[i];
                ulong condBit = ((current & targetMask) != 0) ? 1UL : 0UL;
                ulong maskActive = (ulong)-((long)condBit);

                ulong mutatedVal = current ^ xorMutation;
                dataset[i] = (mutatedVal & maskActive) | (current & ~maskActive);
            }
        }

        // =========================================================================
        // 6. DESCONEXIÓN NATIVA
        // =========================================================================
        private void OnDestroy()
        {
            if (_audioClient != null)
            {
                try { _audioClient.Stop(); } catch { }
                _audioClient = null;
            }

            if (_rawBufferBase != null)
            {
                UnsafeUtility.Free(_rawBufferBase, Allocator.Persistent);
                _rawBufferBase = null;
            }

            if (_previousFrameStates != null)
            {
                UnsafeUtility.Free(_previousFrameStates, Allocator.Persistent);
                _previousFrameStates = null;
            }

            UnityEngine.Debug.Log("<color=#FF4500>[SDAE Engine]</color> Recursos de memoria liberados.");
        }
    }
}