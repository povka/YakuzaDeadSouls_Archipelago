namespace YakuzaDeadSouls.Ps3;

public sealed class Notifier(string host)
{
    public const int CcapiPort = 6333;
    public const int MaxLength = 199;

    public enum Icon
    {
        Info = 0,
        Friend = 2,
        Trophy = 12,
    }

    // 1-3 are the physical console buzzer, not UI sounds. Deliberately absent.
    public enum Sound
    {
        None = -1,
        Trophy = 5,
    }

    private static string Trim(string message) =>
        message.Length <= MaxLength ? message : message[..(MaxLength - 1)] + "…";

    public async Task NotifyAsync(string message, Icon icon = Icon.Trophy)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var msg = Uri.EscapeDataString(Trim(message));
        await http.GetAsync($"http://{host}:{CcapiPort}/ccapi/notify?id={(int)icon}&msg={msg}");
    }

    public async Task NotifyWithSoundAsync(string message, Sound sound = Sound.Trophy)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var msg = Uri.EscapeDataString(Trim(message));
        var snd = sound == Sound.None ? "" : ((int)sound).ToString();
        await http.GetAsync($"http://{host}/notify.ps3mapi?msg={msg}&icon=0&snd={snd}");
    }

    public async Task<bool> IsCcapiAvailableAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var r = await http.GetAsync($"http://{host}:{CcapiPort}/ccapi/notify?id=0&msg=");
            return r.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
