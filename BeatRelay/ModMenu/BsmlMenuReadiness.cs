#if NETFRAMEWORK
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.MenuButtons;

namespace BeatRelay.ModMenu;

internal static class BsmlMenuReadiness
{
    public static bool IsReady()
    {
        try
        {
            return MenuButtons.Instance != null
                && BeatSaberUI.MainFlowCoordinator != null;
        }
        catch
        {
            return false;
        }
    }
}
#endif
