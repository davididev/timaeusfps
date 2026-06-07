using UnityEngine;

public class SoundFXPlayer 
{
    const float MIN_DISTANCE_DEFAULT = 2.0f;
    const float MAX_DISTANCE_DEFAULT = 5.0f;

    /// <summary>
    /// This is a more complicated version of AudioSource.PlaySoundAtPoint.  It instantiates the object, plays the sound, and deletes it.
    /// </summary>
    /// <param name="sound_clip">The audio clip to play</param>
    /// <param name="position">Position to play it at</param>
    /// <param name="min_distance_multiplier">Multiplies by the default minimum value</param>
    /// <param name="max_distance_multiplier">Multiplies by the default maximum value</param>
    /// <param name="volume">Volume of the FX</param>
    /// <param name="pitch">Alternate pitch</param>
    public static void PlaySound(AudioClip sound_clip, Vector3 position, float min_distance_multiplier = 1.0f, float max_distance_multiplier = 1.0f, float volume = 1.0f, float pitch = 1.0f)
    {
        GameObject g = new GameObject("TempSound");
        AudioSource src = g.AddComponent<AudioSource>();
        src.clip = sound_clip;
        src.volume = volume;
        src.pitch = pitch;
        src.minDistance = min_distance_multiplier * MIN_DISTANCE_DEFAULT;
        src.maxDistance = max_distance_multiplier * MAX_DISTANCE_DEFAULT;
        src.Play();
        Object.Destroy(g, sound_clip.length + 0.5f);  //Be sure to free it from memory after we play the sound
    }
}
