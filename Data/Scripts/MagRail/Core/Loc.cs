using System;
using System.IO;
using Sandbox.ModAPI;
using VRage;
using VRage.Utils;

namespace MagRail
{
    // Texts live in Data/Localization/MyTexts.resx (English) and MyTexts.<culture>.resx, built from
    // Localization/MagRail_Localization.tsv. The game reads mod .resx files by itself only for
    // campaigns, so the mod loads them into the game's text table (MyTexts) at session start: block
    // names in the .sbc files (DisplayName_... / Description_... keys), terminal titles and the
    // script's texts then come out in the player's language, English for missing ones. Switching
    // the language in the options clears the table; CheckReload loads the texts again.
    public static class Loc
    {
        // Present in MyTexts.resx (added by build_resx.py): tells whether the mod's texts are loaded.
        static readonly MyStringId LoadedMarker = MyStringId.GetOrCompute("MagRail_Loc_Loaded");
        static string folder;

        public static void Load(string modPath)
        {
            if (string.IsNullOrEmpty(modPath))
                return;
            folder = Path.Combine(modPath, "Data", "Localization");
            Reload();
        }

        public static void CheckReload()
        {
            if (folder != null && !MyTexts.Exists(LoadedMarker))
                Reload();
        }

        static void Reload()
        {
            try
            {
                string culture;
                string subculture;
                GetCulture(out culture, out subculture);
                MyTexts.LoadTexts(folder, culture, subculture);
            }
            catch (Exception e)
            {
                Log.Error("Loc.Reload", e);
            }
        }

        // The game's culture names (MyTexts language table).
        static void GetCulture(out string culture, out string subculture)
        {
            culture = null;
            subculture = null;
            MyLanguagesEnum language = MyAPIGateway.Session != null && MyAPIGateway.Session.Config != null
                ? MyAPIGateway.Session.Config.Language : MyLanguagesEnum.English;
            switch (language)
            {
                case MyLanguagesEnum.Czech: culture = "cs"; subculture = "CZ"; break;
                case MyLanguagesEnum.Slovak: culture = "sk"; subculture = "SK"; break;
                case MyLanguagesEnum.German: culture = "de"; break;
                case MyLanguagesEnum.Russian: culture = "ru"; break;
                case MyLanguagesEnum.Spanish_Spain: culture = "es"; break;
                case MyLanguagesEnum.French: culture = "fr"; break;
                case MyLanguagesEnum.Italian: culture = "it"; break;
                case MyLanguagesEnum.Danish: culture = "da"; break;
                case MyLanguagesEnum.Dutch: culture = "nl"; break;
                case MyLanguagesEnum.Icelandic: culture = "is"; subculture = "IS"; break;
                case MyLanguagesEnum.Polish: culture = "pl"; subculture = "PL"; break;
                case MyLanguagesEnum.Finnish: culture = "fi"; break;
                case MyLanguagesEnum.Hungarian: culture = "hu"; subculture = "HU"; break;
                case MyLanguagesEnum.Portuguese_Brazil: culture = "pt"; subculture = "BR"; break;
                case MyLanguagesEnum.Estonian: culture = "et"; subculture = "EE"; break;
                case MyLanguagesEnum.Norwegian: culture = "no"; break;
                case MyLanguagesEnum.Spanish_HispanicAmerica: culture = "es"; subculture = "419"; break;
                case MyLanguagesEnum.Swedish: culture = "sv"; break;
                case MyLanguagesEnum.Catalan: culture = "ca"; subculture = "AD"; break;
                case MyLanguagesEnum.Croatian: culture = "hr"; subculture = "HR"; break;
                case MyLanguagesEnum.Romanian: culture = "ro"; break;
                case MyLanguagesEnum.Ukrainian: culture = "uk"; break;
                case MyLanguagesEnum.Turkish: culture = "tr"; subculture = "TR"; break;
                case MyLanguagesEnum.Latvian: culture = "lv"; break;
                case MyLanguagesEnum.ChineseChina: culture = "zh"; subculture = "CN"; break;
            }
        }

        public static MyStringId Id(string key)
        {
            return MyStringId.GetOrCompute(key);
        }

        public static string T(string key)
        {
            return MyTexts.GetString(MyStringId.GetOrCompute(key));
        }

        // A malformed translation (bad {n}) shows the bare text instead of throwing.
        public static string F(string key, params object[] args)
        {
            string format = T(key);
            try { return string.Format(format, args); }
            catch (FormatException) { return format; }
        }
    }
}
