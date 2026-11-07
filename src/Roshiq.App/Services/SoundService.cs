using System.Windows.Media;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Media;
using Roshiq.Core.Settings;

namespace Roshiq.App.Services;

public enum SoundKind { Reminder, Warning, Event }

/// <summary>
/// Звуки уведомлений. Тоны синтезируются в коде (Core.Media.Tones), один раз пишутся в кэш как WAV
/// и проигрываются через MediaPlayer (с громкостью). Только на потоке интерфейса.
/// </summary>
public sealed class SoundService
{
    private readonly SettingsService _settings;
    private readonly Dictionary<string, MediaPlayer> _players = new();
    private DateTime _lastUtc = DateTime.MinValue;

    public SoundService(SettingsService settings) { _settings = settings; }

    /// <summary>Звук для уведомления этого вида — если звуки включены и прошло достаточно времени с прошлого.</summary>
    public void Play(SoundKind kind)
    {
        var s = _settings.Current.Sounds;
        if (!s.Enabled) return;
        if (DateTime.UtcNow - _lastUtc < TimeSpan.FromSeconds(s.MinGapSec)) return;
        var name = kind switch { SoundKind.Reminder => s.Reminder, SoundKind.Warning => s.Warning, _ => s.Event };
        if (PlayName(name, s.Volume)) _lastUtc = DateTime.UtcNow;
    }

    /// <summary>Прослушать звук (кнопка в настройках) — независимо от того, включены ли звуки.</summary>
    public void Preview(string name, double volume) => PlayName(name, volume);

    private bool PlayName(string name, double volume)
    {
        try
        {
            if (name == "none" || volume <= 0.001) return false;
            if (!_players.TryGetValue(name, out var player))
            {
                var wav = Tones.Wav(name);
                if (wav == null) return false;
                var dir = Path.Combine(AppPaths.Cache, "sounds");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, name + ".wav");
                if (!File.Exists(file) || new FileInfo(file).Length != wav.Length) File.WriteAllBytes(file, wav);
                player = new MediaPlayer();
                player.Open(new Uri(file));
                _players[name] = player;
            }
            player.Volume = Math.Clamp(volume, 0, 1);
            player.Stop();
            player.Position = TimeSpan.Zero;
            player.Play();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Звук: " + ex.Message);
            return false;
        }
    }
}
