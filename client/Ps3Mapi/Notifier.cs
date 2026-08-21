namespace YakuzaDeadSouls.Ps3;

/// <summary>
/// On-screen messages. Toasts render <b>over the running game</b>, not just on
/// the XMB, and they <b>queue</b> rather than replacing each other - so item
/// bursts need no client-side throttle.
/// </summary>
/// <remarks>
/// <para>
/// Two servers, and neither does both things:
/// </para>
/// <list type="bullet">
/// <item>CCAPI on 6333 honours <b>icons</b> but ignores any sound parameter.</item>
/// <item>webMAN on 80 honours <b>sound</b> but ignores its icon parameter
/// entirely - all 51 values draw the generic info "i", and named RCO icons
/// fall back too.</item>
/// </list>
/// <para>
/// A custom Archipelago logo is not reachable from either: every icon
/// parameter is an index or the name of something already inside a firmware
/// <c>.rco</c>, and no API takes an image. The logo waits for a PS3-CKit SPRX
/// drawing in the game's own UI.
/// </para>
/// </remarks>
public sealed class Notifier(string host)
{
    public const int CcapiPort = 6333;

    /// <summary>Message cap, from webMAN's own form.</summary>
    public const int MaxLength = 199;

    /// <summary>
    /// Icon ids <b>as observed on hardware</b>, which is not what CCAPI's
    /// header declares. The header calls id 12 "Finger"; it draws a gold
    /// trophy. Ids 0, 1, 15, 16, 17 and 19 all fall back to the info icon.
    /// An unmapped id degrades silently to info rather than failing, so only
    /// ship an id that has been seen on screen.
    /// </summary>
    public enum Icon
    {
        Info = 0,
        Friend = 2,
        /// <summary>Gold trophy - the right icon for an item landing.</summary>
        Trophy = 12,
    }

    /// <summary>webMAN sound ids. 1-3 are the PHYSICAL console buzzer
    /// (disc-eject/power-on beeper) and are wrong over a game.</summary>
    public enum Sound
    {
        None = -1,
        Trophy = 5,
    }

    private static string Trim(string message) =>
        message.Length <= MaxLength ? message : message[..(MaxLength - 1)] + "…";

    /// <summary>Toast with an icon, via CCAPI. Silent.</summary>
    public async Task NotifyAsync(string message, Icon icon = Icon.Trophy)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var msg = Uri.EscapeDataString(Trim(message));
        await http.GetAsync($"http://{host}:{CcapiPort}/ccapi/notify?id={(int)icon}&msg={msg}");
    }

    /// <summary>Toast with a sound, via webMAN. Icon is always the info "i".</summary>
    public async Task NotifyWithSoundAsync(string message, Sound sound = Sound.Trophy)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var msg = Uri.EscapeDataString(Trim(message));
        var snd = sound == Sound.None ? "" : ((int)sound).ToString();
        await http.GetAsync($"http://{host}/notify.ps3mapi?msg={msg}&icon=0&snd={snd}");
    }

    /// <summary>Is CCAPI answering? Cheap enough to call at startup.</summary>
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
