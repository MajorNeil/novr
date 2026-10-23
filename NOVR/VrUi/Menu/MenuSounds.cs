using System;
using UnityEngine;
using UnityEngine.Audio;
using Object = UnityEngine.Object;

namespace NOVR.VrUi.Menu;

// Quiet ticks for moving the menu focus with the controls and for activating or backing out, so a press is heard as
// taken without looking. Made here and played through the game's menu mixer, so its volume setting covers them.
internal static class MenuSounds
{
    private const int SampleRate = 44100;

    private static AudioSource? _source;
    private static AudioClip? _move;
    private static AudioClip? _select;
    private static AudioClip? _back;
    private static bool _mixerAssigned;

    public static void Move() => Play(ref _move, "NOVR Menu Move", 0.03f, t => Tone(t, 0.03f, 1900f, 1900f), 0.18f);

    public static void Select() => Play(ref _select, "NOVR Menu Select", 0.08f, t => Tone(t, 0.08f, 1250f, 1900f), 0.26f);

    public static void Back() => Play(ref _back, "NOVR Menu Back", 0.08f, t => Tone(t, 0.08f, 1500f, 950f), 0.22f);

    private static void Play(ref AudioClip? clip, string name, float seconds, Func<float, float> wave, float volume)
    {
        try
        {
            if (_source == null)
            {
                var owner = new GameObject("NOVR Menu Sounds");
                Object.DontDestroyOnLoad(owner);
                _source = owner.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.spatialBlend = 0f;
                _mixerAssigned = false;
            }

            if (!_mixerAssigned)
            {
                var mixer = MenuMixer();
                if (mixer != null)
                {
                    _source.outputAudioMixerGroup = mixer;
                    _mixerAssigned = true;
                }
            }

            if (clip == null) clip = CreateClip(name, seconds, wave);
            _source.PlayOneShot(clip, volume);
        }
        catch (Exception)
        {
            // A missing sound is not worth breaking the menu over.
        }
    }

    // A short sweep from one pitch to another with soft edges, so it ticks instead of clicking.
    private static float Tone(float t, float seconds, float fromHertz, float toHertz)
    {
        var edge = Mathf.Min(1f, t / 0.004f, (seconds - t) / 0.02f);
        var hertz = Mathf.Lerp(fromHertz, toHertz, t / seconds);
        return 0.5f * edge * Mathf.Sin(2f * Mathf.PI * hertz * t);
    }

    // The game's sound manager is only there once its main menu has loaded it.
    private static AudioMixerGroup? MenuMixer()
    {
        if (MainMenu.State != MainMenu.LoadingState.Loaded) return null;

        var sound = SoundManager.i;
        return sound != null ? sound.MenuMixer : null;
    }

    private static AudioClip CreateClip(string name, float seconds, Func<float, float> wave)
    {
        var samples = new float[Mathf.RoundToInt(seconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++) samples[i] = wave(i / (float)SampleRate);

        var clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
