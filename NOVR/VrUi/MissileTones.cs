using System;
using UnityEngine;
using UnityEngine.Audio;
using Object = UnityEngine.Object;

namespace NOVR.VrUi;

// Tones for the missiles that need a reaction now, played from where they are. NOVR moves the game's audio listener onto
// the tracked camera (VrCameraManager), so a 3D sound placed in a missile's direction stays there as the head turns.
// Unity's built-in 3D sound only pans left and right, so a beep is pitched down when its missile is behind.
// - A continuous tone for an active radar missile, which turns into a lower, pulsing one while the aircraft is in its
//   notch, so the player can hear when the turn is good enough.
// - Beeps for another kind of missile, quicker as it closes.
// The sounds are made here, and play through the game's missile alert mixer, so its volume setting covers them too.
internal sealed class MissileTones
{
    private const int SampleRate = 44100;
    // Close enough that the volume doesn't fall off, far enough for a clear direction.
    private const float SourceDistance = 2f;
    private const float FadeSeconds = 0.08f;
    private const float BehindPitch = 0.75f;
    private const float ListenerSearchSeconds = 1f;
    private const float ToneHertz = 1000f;
    private const float NotchToneHertz = 600f;
    private const float NotchPulseHertz = 5f;
    private const float BeepHertz = 1400f;
    private const float BeepSeconds = 0.07f;
    private const float BeepEdgeSeconds = 0.008f;
    private const float FastestBeepSeconds = 0.12f;
    private const float SlowestBeepSeconds = 0.6f;

    private GameObject? _toneObject;
    private GameObject? _beepObject;
    private AudioSource? _tone;
    private AudioSource? _notchTone;
    private AudioSource? _beeps;
    private AudioClip? _beepClip;
    private AudioListener? _listener;
    private float _nextListenerSearch;
    private float _nextBeepTime;
    private bool _mixerAssigned;

    // Positions are where the missiles are this frame; null when there is none to sound.
    public void Update(Vector3? activeRadarMissile, bool inNotch, Vector3? otherMissile, float otherDistance, float volume)
    {
        if (activeRadarMissile == null && otherMissile == null)
        {
            Stop();
            return;
        }

        var listener = FindListener();
        if (listener == null)
        {
            Stop();
            return;
        }

        EnsureSources();
        var head = listener.transform;
        if (activeRadarMissile is { } radar) Place(_toneObject!, head, radar);
        Fade(_tone!, activeRadarMissile != null && !inNotch ? volume : 0f);
        Fade(_notchTone!, activeRadarMissile != null && inNotch ? volume : 0f);

        if (otherMissile is not { } other) return;

        Place(_beepObject!, head, other);
        var now = Time.unscaledTime;
        if (now < _nextBeepTime) return;

        _beeps!.pitch = Vector3.Dot(other - head.position, head.forward) < 0f ? BehindPitch : 1f;
        _beeps.PlayOneShot(_beepClip, volume);
        _nextBeepTime = now + Mathf.Lerp(FastestBeepSeconds, SlowestBeepSeconds, Mathf.InverseLerp(800f, 5000f, otherDistance));
    }

    public void Stop()
    {
        if (_tone != null && _tone.isPlaying) _tone.Stop();
        if (_notchTone != null && _notchTone.isPlaying) _notchTone.Stop();
        if (_tone != null) _tone.volume = 0f;
        if (_notchTone != null) _notchTone.volume = 0f;
    }

    public void Dispose()
    {
        if (_toneObject != null) Object.Destroy(_toneObject);
        if (_beepObject != null) Object.Destroy(_beepObject);
    }

    private static void Place(GameObject source, Transform head, Vector3 missile)
    {
        var direction = missile - head.position;
        if (direction.sqrMagnitude < 1e-4f) direction = head.forward;
        source.transform.position = head.position + direction.normalized * SourceDistance;
    }

    // Quick fades, so switching between the tones doesn't click.
    private static void Fade(AudioSource source, float target)
    {
        source.volume = Mathf.MoveTowards(source.volume, target, Time.unscaledDeltaTime / FadeSeconds);
        if (source.volume > 0f && !source.isPlaying) source.Play();
        else if (source.volume <= 0f && source.isPlaying) source.Stop();
    }

    private AudioListener? FindListener()
    {
        if (_listener != null && _listener.isActiveAndEnabled) return _listener;
        if (Time.unscaledTime < _nextListenerSearch) return null;

        _nextListenerSearch = Time.unscaledTime + ListenerSearchSeconds;
        foreach (var listener in Object.FindObjectsOfType<AudioListener>())
        {
            if (listener.isActiveAndEnabled) return _listener = listener;
        }

        return null;
    }

    private void EnsureSources()
    {
        if (_toneObject == null)
        {
            _mixerAssigned = false;
            _toneObject = new GameObject("NOVR Missile Tone");
            Object.DontDestroyOnLoad(_toneObject);
            // A second of each, a whole number of cycles of everything in it, so it loops without a click.
            _tone = AddSource(_toneObject, CreateClip("NOVR Missile Tone", 1f,
                t => 0.3f * Mathf.Sin(2f * Mathf.PI * ToneHertz * t) + 0.08f * Mathf.Sin(4f * Mathf.PI * ToneHertz * t)), loop: true);
            _notchTone = AddSource(_toneObject, CreateClip("NOVR Notch Tone", 1f,
                t => 0.38f * (0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * NotchPulseHertz * t)) * Mathf.Sin(2f * Mathf.PI * NotchToneHertz * t)), loop: true);
        }

        if (_beepObject == null)
        {
            _mixerAssigned = false;
            _beepObject = new GameObject("NOVR Missile Beeps");
            Object.DontDestroyOnLoad(_beepObject);
            _beepClip = CreateClip("NOVR Missile Beep", BeepSeconds,
                t => 0.4f * Mathf.Min(1f, t / BeepEdgeSeconds, (BeepSeconds - t) / BeepEdgeSeconds) * Mathf.Sin(2f * Mathf.PI * BeepHertz * t));
            _beeps = AddSource(_beepObject, null, loop: false);
        }

        // The game's sound manager may come up after the sources were made.
        if (_mixerAssigned) return;
        var mixer = MissileAlertMixer();
        if (mixer == null) return;
        _tone!.outputAudioMixerGroup = mixer;
        _notchTone!.outputAudioMixerGroup = mixer;
        _beeps!.outputAudioMixerGroup = mixer;
        _mixerAssigned = true;
    }

    private static AudioSource AddSource(GameObject owner, AudioClip? clip, bool loop)
    {
        var source = owner.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        source.clip = clip;
        source.volume = 0f;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.spread = 0f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 50f;
        source.maxDistance = 100f;
        return source;
    }

    private static AudioMixerGroup? MissileAlertMixer()
    {
        try
        {
            var sound = SoundManager.i;
            return sound != null ? sound.MissileAlertMixer : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // wave(t) for t in seconds from the start.
    private static AudioClip CreateClip(string name, float seconds, Func<float, float> wave)
    {
        var samples = new float[Mathf.RoundToInt(seconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++) samples[i] = wave(i / (float)SampleRate);

        var clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
