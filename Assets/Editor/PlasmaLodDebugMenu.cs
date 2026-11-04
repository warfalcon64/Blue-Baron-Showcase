using UnityEditor;

// Play-mode verification toggles for the plasma viewport LOD (statics reset on domain reload).
// Tint + the Scene view show the tier split live; Invert is the positive control — with it on,
// on-screen bolts visibly stutter at the low rate, proving the tier machinery actually works.
public static class PlasmaLodDebugMenu
{
    private const string TintPath = "Tools/Plasma LOD/Tint Bolts By Tier (Scene view)";
    private const string InvertPath = "Tools/Plasma LOD/Invert Tiers (Positive Control)";
    private const string OverlayPath = "Tools/Plasma LOD/Show Count Overlay";

    [MenuItem(TintPath)]
    private static void ToggleTint()
    {
        WeaponsPlasma.TintByLodTier = !WeaponsPlasma.TintByLodTier;
        Menu.SetChecked(TintPath, WeaponsPlasma.TintByLodTier);
    }

    [MenuItem(InvertPath)]
    private static void ToggleInvert()
    {
        ProjectileManager.InvertLodTiers = !ProjectileManager.InvertLodTiers;
        Menu.SetChecked(InvertPath, ProjectileManager.InvertLodTiers);
    }

    [MenuItem(OverlayPath)]
    private static void ToggleOverlay()
    {
        ProjectileManager.ShowDebugOverlay = !ProjectileManager.ShowDebugOverlay;
        Menu.SetChecked(OverlayPath, ProjectileManager.ShowDebugOverlay);
    }
}
