using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace MultiplayerTestHarness
{
    internal static class NetworkTestMenu
    {
        const string Label = "Network Test";
        const string HarmonyId = "RaftMod.MultiplayerTest.Menu";

        static readonly FieldInfo NewDropdownField =
            AccessTools.Field(typeof(NewGameBox), "authSettingDropdown");

        static readonly FieldInfo LoadDropdownField =
            AccessTools.Field(typeof(LoadGameBox), "authSettingDropdown");

        static Harmony _harmony;

        internal static void Install()
        {
            if (_harmony != null)
                return;

            _harmony = new Harmony(HarmonyId);
            _harmony.CreateClassProcessor(typeof(Patch_NewGameBox_Update)).Patch();
            _harmony.CreateClassProcessor(typeof(Patch_LoadGameBox_Update)).Patch();
            _harmony.CreateClassProcessor(typeof(Patch_NewGameBox_Create)).Patch();
            _harmony.CreateClassProcessor(typeof(Patch_LoadGameBox_Load)).Patch();
        }

        internal static void Uninstall()
        {
            if (_harmony == null)
                return;

            _harmony.UnpatchAll(HarmonyId);
            _harmony = null;
        }

        static object GetDropdown(NewGameBox box)
        {
            return NewDropdownField != null
                ? NewDropdownField.GetValue(box)
                : null;
        }

        static object GetDropdown(LoadGameBox box)
        {
            return LoadDropdownField != null
                ? LoadDropdownField.GetValue(box)
                : null;
        }

        static IList GetOptions(object dropdown)
        {
            if (dropdown == null)
                return null;

            PropertyInfo property = dropdown.GetType().GetProperty(
                "options",
                BindingFlags.Instance | BindingFlags.Public);

            return property != null
                ? property.GetValue(dropdown, null) as IList
                : null;
        }

        static int GetValue(object dropdown)
        {
            if (dropdown == null)
                return -1;

            PropertyInfo property = dropdown.GetType().GetProperty(
                "value",
                BindingFlags.Instance | BindingFlags.Public);

            if (property == null)
                return -1;

            object value = property.GetValue(dropdown, null);
            return value is int ? (int)value : -1;
        }

        static void SetValue(object dropdown, int value)
        {
            if (dropdown == null)
                return;

            PropertyInfo property = dropdown.GetType().GetProperty(
                "value",
                BindingFlags.Instance | BindingFlags.Public);

            if (property != null)
                property.SetValue(dropdown, value, null);
        }

        static string GetOptionText(object option)
        {
            if (option == null)
                return null;

            PropertyInfo property = option.GetType().GetProperty(
                "text",
                BindingFlags.Instance | BindingFlags.Public);

            return property != null
                ? property.GetValue(option, null) as string
                : null;
        }

        static object CreateOption(IList options, string text)
        {
            if (options == null)
                return null;

            Type listType = options.GetType();
            Type[] genericArguments = listType.GetGenericArguments();

            if (genericArguments.Length != 1)
                return null;

            Type optionType = genericArguments[0];

            ConstructorInfo constructor = optionType.GetConstructor(
                new[] { typeof(string) });

            if (constructor != null)
                return constructor.Invoke(new object[] { text });

            object option = Activator.CreateInstance(optionType);

            PropertyInfo property = optionType.GetProperty(
                "text",
                BindingFlags.Instance | BindingFlags.Public);

            if (property != null)
                property.SetValue(option, text, null);

            return option;
        }

        static void Refresh(object dropdown)
        {
            if (dropdown == null)
                return;

            MethodInfo method = dropdown.GetType().GetMethod(
                "RefreshShownValue",
                BindingFlags.Instance | BindingFlags.Public);

            if (method != null)
                method.Invoke(dropdown, null);
        }

        static void EnsureOption(object dropdown)
        {
            IList options = GetOptions(dropdown);
            if (options == null)
                return;

            for (int i = 0; i < options.Count; i++)
            {
                if (GetOptionText(options[i]) == Label)
                    return;
            }

            object option = CreateOption(options, Label);
            if (option == null)
                return;

            options.Add(option);
            Refresh(dropdown);
        }

        static bool IsSelected(object dropdown)
        {
            IList options = GetOptions(dropdown);
            int value = GetValue(dropdown);

            if (options == null || value < 0 || value >= options.Count)
                return false;

            return GetOptionText(options[value]) == Label;
        }

        static void BeginHost(object dropdown)
        {
            if (!IsSelected(dropdown))
                return;

            if (!NetworkTest.StartHost())
                return;

            // В оригинальную загрузку передаётся валидный vanilla auth mode
            SetValue(dropdown, 2);
            Refresh(dropdown);
        }

        [HarmonyPatch(typeof(NewGameBox), "Update")]
        static class Patch_NewGameBox_Update
        {
            static void Postfix(NewGameBox __instance)
            {
                EnsureOption(GetDropdown(__instance));
            }
        }

        [HarmonyPatch(typeof(LoadGameBox), "Update")]
        static class Patch_LoadGameBox_Update
        {
            static void Postfix(LoadGameBox __instance)
            {
                EnsureOption(GetDropdown(__instance));
            }
        }

        [HarmonyPatch(typeof(NewGameBox), "Button_CreateNewGame")]
        static class Patch_NewGameBox_Create
        {
            static void Prefix(NewGameBox __instance)
            {
                BeginHost(GetDropdown(__instance));
            }
        }

        [HarmonyPatch(typeof(LoadGameBox), "Button_LoadGame")]
        static class Patch_LoadGameBox_Load
        {
            static void Prefix(LoadGameBox __instance)
            {
                BeginHost(GetDropdown(__instance));
            }
        }
    }
}
