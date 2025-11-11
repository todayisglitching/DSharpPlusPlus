using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace DSharpPlusPlus.Commands.Processors.SlashCommands.Localization;

public static class LocalesHelper
{
    public static IReadOnlyDictionary<string, DiscordLocale> EnglishToLocale { get; }
    public static IReadOnlyDictionary<string, DiscordLocale> NativeToLocale { get; }
    public static IReadOnlyDictionary<DiscordLocale, string> LocaleToEnglish { get; }
    public static IReadOnlyDictionary<DiscordLocale, string> LocaleToNative { get; }

    static LocalesHelper()
    {
        Dictionary<string, DiscordLocale> englishToLocale = new()
        {
            ["Indonesian"] = DiscordLocale.Id,
            ["Danish"] = DiscordLocale.Da,
            ["German"] = DiscordLocale.De,
            ["English, UK"] = DiscordLocale.EnGb,
            ["English, US"] = DiscordLocale.EnUs,
            ["Spanish"] = DiscordLocale.EsEs,
            ["French"] = DiscordLocale.Fr,
            ["Croatian"] = DiscordLocale.Hr,
            ["Italian"] = DiscordLocale.It,
            ["Lithuanian"] = DiscordLocale.Lt,
            ["Hungarian"] = DiscordLocale.Hu,
            ["Dutch"] = DiscordLocale.Nl,
            ["Norwegian"] = DiscordLocale.No,
            ["Polish"] = DiscordLocale.Pl,
            ["Portuguese"] = DiscordLocale.PtBr,
            ["Romanian"] = DiscordLocale.Ro,
            ["Finnish"] = DiscordLocale.Fi,
            ["Swedish"] = DiscordLocale.SvSe,
            ["Vietnamese"] = DiscordLocale.Vi,
            ["Turkish"] = DiscordLocale.Tr,
            ["Czech"] = DiscordLocale.Cs,
            ["Greek"] = DiscordLocale.El,
            ["Bulgarian"] = DiscordLocale.Bg,
            ["Russian"] = DiscordLocale.Ru,
            ["Ukrainian"] = DiscordLocale.Uk,
            ["Hindi"] = DiscordLocale.Hi,
            ["Thai"] = DiscordLocale.Th,
            ["Chinese, China"] = DiscordLocale.ZhCn,
            ["Japanese"] = DiscordLocale.Ja,
            ["Chinese"] = DiscordLocale.ZhTw,
            ["Korean"] = DiscordLocale.Ko,
        };

        Dictionary<string, DiscordLocale> nativeToLocale = new()
        {
            ["Bahasa Indonesia"] = DiscordLocale.Id,
            ["Dansk"] = DiscordLocale.Da,
            ["Deutsch"] = DiscordLocale.De,
            ["English, UK"] = DiscordLocale.EnGb,
            ["English, US"] = DiscordLocale.EnUs,
            ["Español"] = DiscordLocale.EsEs,
            ["Français"] = DiscordLocale.Fr,
            ["Hrvatski"] = DiscordLocale.Hr,
            ["Italiano"] = DiscordLocale.It,
            ["Lietuviškai"] = DiscordLocale.Lt,
            ["Magyar"] = DiscordLocale.Hu,
            ["Nederlands"] = DiscordLocale.Nl,
            ["Norsk"] = DiscordLocale.No,
            ["Polski"] = DiscordLocale.Pl,
            ["Português do Brasil"] = DiscordLocale.PtBr,
            ["Română"] = DiscordLocale.Ro,
            ["Suomi"] = DiscordLocale.Fi,
            ["Svenska"] = DiscordLocale.SvSe,
            ["Tiếng Việt"] = DiscordLocale.Vi,
            ["Türkçe"] = DiscordLocale.Tr,
            ["Čeština"] = DiscordLocale.Cs,
            ["Ελληνικά"] = DiscordLocale.El,
            ["български"] = DiscordLocale.Bg,
            ["Pусский"] = DiscordLocale.Ru,
            ["Українська"] = DiscordLocale.Uk,
            ["हिन्दी"] = DiscordLocale.Hi,
            ["ไทย"] = DiscordLocale.Th,
            ["中文"] = DiscordLocale.ZhCn,
            ["日本語"] = DiscordLocale.Ja,
            ["繁體中文"] = DiscordLocale.ZhTw,
            ["한국어"] = DiscordLocale.Ko,
        };

        Dictionary<DiscordLocale, string> localeToEnglish = englishToLocale.ToDictionary(x => x.Value, x => x.Key);
        Dictionary<DiscordLocale, string> localeToNative = nativeToLocale.ToDictionary(x => x.Value, x => x.Key);

        EnglishToLocale = englishToLocale.ToFrozenDictionary();
        NativeToLocale = nativeToLocale.ToFrozenDictionary();
        LocaleToEnglish = localeToEnglish.ToFrozenDictionary();
        LocaleToNative = localeToNative.ToFrozenDictionary();
    }
}
