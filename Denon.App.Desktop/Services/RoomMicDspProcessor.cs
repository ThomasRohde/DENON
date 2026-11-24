using Denon.App.Core.Services.Metering;
using Denon.App.PluginSdk;
using NAudio.Wave;

namespace Denon.App.Desktop.Services;

/// <summary>
/// Studio-grade DSP front-end for opt-in "ROOM" mode (PC microphone / Line-In):
/// <list type="bullet">
///   <item>Supports 32-bit IEEE float, 16-bit PCM, 24-bit PCM, 32-bit PCM, and <see cref="WaveFormatExtensible"/>.</item>
///   <item>35 Hz DC-blocking high-pass filter removes desk vibration and HVAC rumble.</item>
///   <item>Adaptive noise-floor estimator + smooth soft-knee noise gate so quiet rooms rest cleanly at 0 (−20 VU).</item>
///   <item>Smooth Auto-Gain Control (AGC: fast attack, slow release, gated so silence is never boosted) keeps both quiet and loud Denon volumes naturally in the VU sweet spot.</item>
///   <item>Preserves true stereo L/R separation for 2+ channel mics / Line-In, while adding subtle organic L/R decorrelation for 1-channel mono mics.</item>
/// </list>
/// </summary>
public sealed class RoomMicDspProcessor
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    // Adaptive noise-floor bounds (linear RMS)
    private const double InitialNoiseFloor = 0.0008; // ~ -62 dBFS
    private const double MinNoiseFloor = 0.00025;    // ~ -72 dBFS
    private const double MaxNoiseFloor = 0.08;       // ~ -22 dBFS

    // Noise gate thresholds relative to adaptive floor
    private const double GateOpenRatio = 2.4;        // +7.6 dB above floor -> fully open
    private const double GateCloseRatio = 1.35;      // +2.6 dB above floor -> fully closed
    private const double MinGateOpenRms = 0.0018;    // -55 dBFS absolute minimum open threshold
    private const double MinGateCloseRms = 0.0010;   // -60 dBFS absolute minimum close threshold
    private const double GateHoldSeconds = 0.18;

    // Auto-Gain Control (AGC) parameters: target RMS puts typical music naturally into -6..0 VU sweet spot
    private const double AgcTargetRms = 0.24;
    private const double AgcMinGain = 0.25;          // -12 dB attenuation for very loud sources
    private const double AgcMaxGain = 36.0;          // +31 dB boost for quiet evening listening
    private const double AgcAttackTauSec = 0.045;    // Fast attack when level jumps up
    private const double AgcReleaseTauSec = 1.60;    // Slow release when level drops down

    private readonly AudioSpectrumAnalyzer _spectrum = new();

    // DC-blocking high-pass filter state (35 Hz)
    private double _hpPrevInL;
    private double _hpPrevOutL;
    private double _hpPrevInR;
    private double _hpPrevOutR;

    // Adaptive noise floor, gate, and AGC state
    private double _noiseFloorRms = InitialNoiseFloor;
    private double _gateGain;
    private double _gateHoldRemaining;
    private double _agcGain = 4.0;

    // Minimum statistics for the noise floor: the quietest level per 0.5 s block over the last 8 s.
    private const double FloorBlockSeconds = 0.5;
    private readonly double[] _floorBlockMins = new double[16];
    private int _floorBlockCount;
    private int _floorBlockNext;
    private double _floorBlockMin = double.MaxValue;
    private double _floorBlockElapsed;

    // Subtle organic L/R separation state for 1-channel mono mics (1-pole ~650 Hz split + gentle drift)
    private double _monoLowState;
    private double _monoTiltSmooth;
    private double _monoDriftPhase;

    public double NoiseFloorRms => _noiseFloorRms;

    public double GateGain => _gateGain;

    public double AgcGain => _agcGain;

    public bool IsGateOpen => _gateGain > 0.005;

    public void Reset()
    {
        _hpPrevInL = _hpPrevOutL = 0;
        _hpPrevInR = _hpPrevOutR = 0;
        _noiseFloorRms = InitialNoiseFloor;
        _gateGain = 0;
        _gateHoldRemaining = 0;
        _agcGain = 4.0;
        _floorBlockCount = 0;
        _floorBlockNext = 0;
        _floorBlockMin = double.MaxValue;
        _floorBlockElapsed = 0;
        _monoLowState = 0;
        _monoTiltSmooth = 0;
        _monoDriftPhase = 0;
        _spectrum.Reset();
    }

    /// <summary>
    /// Processes a raw WASAPI capture buffer and produces a studio-conditioned <see cref="PluginAudioFrame"/>
    /// plus 31-band RTA spectrum into <paramref name="bands31Destination"/>.
    /// </summary>
    public PluginAudioFrame ProcessBuffer(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat format,
        long timestampMs,
        Span<double> bands31Destination)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameSize = channels * bytesPerSample;
        if (bytesRecorded < frameSize || frameSize <= 0)
        {
            ClearBands(bands31Destination);
            return new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, timestampMs);
        }

        var isFloat = IsFloatFormat(format);
        var sampleRate = Math.Max(8000, format.SampleRate);
        var frames = bytesRecorded / frameSize;
        var dt = Math.Clamp(frames / (double)sampleRate, 0.001, 0.25);

        // 1st-order high-pass coefficient at 35 Hz
        var hpAlpha = 1.0 / (1.0 + (2.0 * Math.PI * 35.0 / sampleRate));
        // 1-pole low-pass at ~650 Hz for mono organic spectral tilt
        var monoLpAlpha = 1.0 - Math.Exp(-2.0 * Math.PI * 650.0 / sampleRate);

        // Pass 1: DC-block + measure raw block RMS & peak (and mono low/high split if 1-channel)
        double sumL = 0, sumR = 0, peakL = 0, peakR = 0;
        double monoLowSq = 0, monoHighSq = 0;

        // First compute block stats so AGC + noise gate act cleanly on the whole block
        // (and store HP-filtered mono samples in a small stack/local pass via a second lightweight read).
        var hpInL = _hpPrevInL;
        var hpOutL = _hpPrevOutL;
        var hpInR = _hpPrevInR;
        var hpOutR = _hpPrevOutR;
        var monoLow = _monoLowState;

        for (var f = 0; f < frames; f++)
        {
            var offset = f * frameSize;
            double rawL = ReadNormalizedSample(buffer, offset, bytesPerSample, isFloat);
            var filteredL = hpAlpha * (hpOutL + rawL - hpInL);
            hpInL = rawL;
            hpOutL = filteredL;

            double filteredR;
            if (channels > 1)
            {
                double rawR = ReadNormalizedSample(buffer, offset + bytesPerSample, bytesPerSample, isFloat);
                filteredR = hpAlpha * (hpOutR + rawR - hpInR);
                hpInR = rawR;
                hpOutR = filteredR;
            }
            else
            {
                filteredR = filteredL;
                monoLow += (filteredL - monoLow) * monoLpAlpha;
                var monoHigh = filteredL - monoLow;
                monoLowSq += monoLow * monoLow;
                monoHighSq += monoHigh * monoHigh;
            }

            var absL = Math.Abs(filteredL);
            var absR = Math.Abs(filteredR);
            if (absL > peakL) peakL = absL;
            if (absR > peakR) peakR = absR;
            sumL += filteredL * filteredL;
            sumR += filteredR * filteredR;
        }

        var rmsL = Math.Sqrt(sumL / frames);
        var rmsR = Math.Sqrt(sumR / frames);
        var monoRms = Math.Sqrt(0.5 * ((rmsL * rmsL) + (rmsR * rmsR)));
        var monoPeak = Math.Max(peakL, peakR);

        // Update adaptive noise-floor estimator
        UpdateNoiseFloor(monoRms, dt);

        // Evaluate soft-knee noise gate
        var gateClose = Math.Max(_noiseFloorRms * GateCloseRatio, MinGateCloseRms);
        var gateOpen = Math.Max(_noiseFloorRms * GateOpenRatio, MinGateOpenRms);
        UpdateNoiseGate(monoRms, gateClose, gateOpen, dt);

        // Update AGC ONLY while gate is open so silence / room hiss is never amplified
        if (_gateGain > 0.35 && monoRms > gateClose)
        {
            UpdateAgc(monoRms, monoPeak, dt);
        }

        var effectiveGain = _agcGain * _gateGain;

        // Pass 2: replay HP filter from saved pre-block state to feed exact scaled mono samples into FFT ring
        if (effectiveGain <= 0.0005)
        {
            // Commit filter states and keep FFT cleanly at zero so quiet rooms rest at 0 (-20 VU)
            _hpPrevInL = hpInL;
            _hpPrevOutL = hpOutL;
            _hpPrevInR = hpInR;
            _hpPrevOutR = hpOutR;
            _monoLowState = monoLow;
            _monoTiltSmooth *= Math.Exp(-dt / 0.15);

            var zeroSamples = Math.Min(frames, AudioMeterMath.FftSize);
            for (var i = 0; i < zeroSamples; i++)
            {
                _spectrum.PushMonoSample(0f);
            }

            ClearBands(bands31Destination);
            return new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, timestampMs);
        }

        // Feed gain-conditioned, soft-limited mono samples into the shared 512-pt FFT ring
        hpInL = _hpPrevInL;
        hpOutL = _hpPrevOutL;
        hpInR = _hpPrevInR;
        hpOutR = _hpPrevOutR;
        for (var f = 0; f < frames; f++)
        {
            var offset = f * frameSize;
            double rawL = ReadNormalizedSample(buffer, offset, bytesPerSample, isFloat);
            var filteredL = hpAlpha * (hpOutL + rawL - hpInL);
            hpInL = rawL;
            hpOutL = filteredL;

            double mono;
            if (channels > 1)
            {
                double rawR = ReadNormalizedSample(buffer, offset + bytesPerSample, bytesPerSample, isFloat);
                var filteredR = hpAlpha * (hpOutR + rawR - hpInR);
                hpInR = rawR;
                hpOutR = filteredR;
                mono = (filteredL + filteredR) * 0.5;
            }
            else
            {
                mono = filteredL;
            }

            var scaled = SoftLimit(mono * effectiveGain);
            _spectrum.PushMonoSample((float)scaled);
        }

        _hpPrevInL = hpInL;
        _hpPrevOutL = hpOutL;
        _hpPrevInR = hpInR;
        _hpPrevOutR = hpOutR;
        _monoLowState = monoLow;

        // Compute normalized VU levels with soft-limited peaks & RMS
        var outPeakL = SoftLimit(peakL * effectiveGain);
        var outRmsL = SoftLimit(rmsL * effectiveGain);
        var outPeakR = SoftLimit(peakR * effectiveGain);
        var outRmsR = SoftLimit(rmsR * effectiveGain);

        var leftLevel = AudioMeterMath.ToNormalizedLevel(outPeakL, outRmsL);
        var rightLevel = AudioMeterMath.ToNormalizedLevel(outPeakR, outRmsR);

        // Subtle organic L/R separation ONLY for 1-channel mono mics; stereo mics / Line-In preserve exact L/R!
        if (channels == 1 && leftLevel > 0.0)
        {
            var lowRms = Math.Sqrt(monoLowSq / frames);
            var highRms = Math.Sqrt(monoHighSq / frames);
            var totalBand = lowRms + highRms + 1e-6;
            // Spectral tilt in [-1, +1]: bass-heavy nudges slightly left, bright transients nudge slightly right
            var rawTilt = (lowRms - highRms) / totalBand;
            var tiltAlpha = 1.0 - Math.Exp(-dt / 0.08);
            _monoTiltSmooth += (rawTilt - _monoTiltSmooth) * tiltAlpha;

            // Gentle energy-coupled drift so even steady mono content has subtle, natural dual-needle life
            _monoDriftPhase += dt * (1.8 + (leftLevel * 2.4));
            if (_monoDriftPhase > Math.PI * 200.0)
            {
                _monoDriftPhase -= Math.PI * 200.0;
            }

            var drift = Math.Sin(_monoDriftPhase) * 0.028 + Math.Cos(_monoDriftPhase * 0.43) * 0.017;
            var spread = Math.Clamp((_monoTiltSmooth * 0.045) + drift, -0.075, 0.075);

            var baseLevel = leftLevel;
            leftLevel = Math.Clamp(baseLevel * (1.0 + spread), 0.0, 1.0);
            rightLevel = Math.Clamp(baseLevel * (1.0 - spread), 0.0, 1.0);
        }

        var isLive = leftLevel > 0.003 || rightLevel > 0.003;
        return _spectrum.Analyze(sampleRate, leftLevel, rightLevel, isLive, timestampMs, bands31Destination);
    }

    /// <summary>
    /// Noise floor by minimum statistics. It falls quickly when the room gets quieter. It rises only toward the
    /// quietest level of the last 8 s, so program audio (which has dips between words and notes) does not become
    /// "noise" and close the gate, while a steady fan or HVAC hum is learned within a few seconds.
    /// </summary>
    private void UpdateNoiseFloor(double monoRms, double dt)
    {
        if (monoRms <= 1e-7)
        {
            // True digital silence: gently relax floor toward minimum
            var alphaZero = 1.0 - Math.Exp(-dt / 0.50);
            _noiseFloorRms = Math.Clamp(_noiseFloorRms + ((MinNoiseFloor - _noiseFloorRms) * alphaZero), MinNoiseFloor, MaxNoiseFloor);
            return;
        }

        _floorBlockMin = Math.Min(_floorBlockMin, monoRms);
        _floorBlockElapsed += dt;
        if (_floorBlockElapsed >= FloorBlockSeconds)
        {
            _floorBlockMins[_floorBlockNext] = _floorBlockMin;
            _floorBlockNext = (_floorBlockNext + 1) % _floorBlockMins.Length;
            _floorBlockCount = Math.Min(_floorBlockCount + 1, _floorBlockMins.Length);
            _floorBlockMin = double.MaxValue;
            _floorBlockElapsed = 0;
        }

        if (monoRms < _noiseFloorRms)
        {
            // Room got quieter: track downward fairly quickly
            var alphaDown = 1.0 - Math.Exp(-dt / 0.35);
            _noiseFloorRms = Math.Clamp(_noiseFloorRms + ((monoRms - _noiseFloorRms) * alphaDown), MinNoiseFloor, MaxNoiseFloor);
            return;
        }

        var windowMin = _floorBlockMin;
        for (var i = 0; i < _floorBlockCount; i++)
        {
            windowMin = Math.Min(windowMin, _floorBlockMins[i]);
        }

        // Close to the floor (within ~8 dB) the floor follows at once. A larger rise needs a full 8 s window of
        // evidence, so the first seconds of a quiet program are not taken for noise.
        var windowFull = _floorBlockCount == _floorBlockMins.Length;
        if (windowMin > _noiseFloorRms && windowMin < double.MaxValue &&
            (windowMin < _noiseFloorRms * 2.5 || windowFull))
        {
            var alphaUp = 1.0 - Math.Exp(-dt / 2.0);
            _noiseFloorRms = Math.Clamp(_noiseFloorRms + ((windowMin - _noiseFloorRms) * alphaUp), MinNoiseFloor, MaxNoiseFloor);
        }
    }

    private void UpdateNoiseGate(double monoRms, double gateClose, double gateOpen, double dt)
    {
        double targetGate;
        if (monoRms >= gateOpen)
        {
            targetGate = 1.0;
            _gateHoldRemaining = GateHoldSeconds;
        }
        else if (monoRms <= gateClose)
        {
            if (_gateHoldRemaining > 0)
            {
                _gateHoldRemaining = Math.Max(0, _gateHoldRemaining - dt);
                targetGate = _gateGain; // hold steady during short musical/dialogue gaps
            }
            else
            {
                targetGate = 0.0;
            }
        }
        else
        {
            // Soft-knee cubic smoothstep between gateClose and gateOpen
            var span = Math.Max(1e-6, gateOpen - gateClose);
            var t = Math.Clamp((monoRms - gateClose) / span, 0.0, 1.0);
            targetGate = t * t * (3.0 - (2.0 * t));
            if (targetGate > 0.5)
            {
                _gateHoldRemaining = Math.Max(_gateHoldRemaining, GateHoldSeconds * 0.5);
            }
        }

        var tau = targetGate >= _gateGain ? 0.012 : 0.20;
        var alpha = 1.0 - Math.Exp(-dt / tau);
        _gateGain += (targetGate - _gateGain) * alpha;
        if (_gateGain < 0.005 && targetGate == 0.0)
        {
            _gateGain = 0.0;
        }
    }

    private void UpdateAgc(double monoRms, double monoPeak, double dt)
    {
        var desired = Math.Clamp(AgcTargetRms / Math.Max(monoRms, 1e-5), AgcMinGain, AgcMaxGain);
        if (monoPeak > 1e-5 && (monoPeak * desired) > 0.96)
        {
            desired = Math.Max(AgcMinGain, Math.Min(desired, 0.96 / monoPeak));
        }

        // Fast attack when attenuating louder passages, slow release when boosting quieter passages
        var tau = desired < _agcGain ? AgcAttackTauSec : AgcReleaseTauSec;
        var alpha = 1.0 - Math.Exp(-dt / tau);
        _agcGain = Math.Clamp(_agcGain + ((desired - _agcGain) * alpha), AgcMinGain, AgcMaxGain);
    }

    private static double SoftLimit(double value)
    {
        var abs = Math.Abs(value);
        if (abs <= 0.85)
        {
            return value;
        }

        // Smooth knee compression above 0.85 asymptotically approaching 1.0
        var excess = abs - 0.85;
        var limited = 0.85 + (0.15 * Math.Tanh(excess / 0.15));
        return value < 0 ? -limited : limited;
    }

    internal static bool IsFloatFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);

    internal static float ReadNormalizedSample(byte[] buffer, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat && bytesPerSample == 4)
        {
            var v = BitConverter.ToSingle(buffer, offset);
            return float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
        }

        if (isFloat && bytesPerSample == 8)
        {
            var v = BitConverter.ToDouble(buffer, offset);
            return double.IsFinite(v) ? (float)Math.Clamp(v, -1.0, 1.0) : 0f;
        }

        if (bytesPerSample == 2)
        {
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        }

        if (bytesPerSample == 3)
        {
            var sample24 = buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16);
            return sample24 / 8388608f;
        }

        if (bytesPerSample == 4)
        {
            return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        }

        return 0f;
    }

    private static void ClearBands(Span<double> destination)
    {
        if (destination.Length >= AudioMeterMath.BandCount)
        {
            destination[..AudioMeterMath.BandCount].Clear();
        }
        else if (!destination.IsEmpty)
        {
            destination.Clear();
        }
    }
}
