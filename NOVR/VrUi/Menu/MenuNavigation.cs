using System;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// The menu controls' joystick buttons in menus that aren't NOVR's own screens: the game's encyclopedia and key binding
// screen, and NOVR's older single player, multiplayer and workshop panels. The directions move the VR cursor from
// button to button (VrUiCursor.Navigate), Select clicks where it is, Back presses the screen's Cancel, Back, Close or
// Done button, and Previous/Next Tab page the list or text under the cursor.
// Joystick only: some of these menus answer keys and the gamepad themselves, and a key doing both would act twice.
// Out of flight only (the main menu and the encyclopedia), so a hat bound here can't also be trimming an aircraft or
// pressing a mission's way back to the menu.
internal static class MenuNavigation
{
    private static bool _loggedError;

    public static bool Wanted =>
        VrUiCursor.I is { IsActive: true } && GameManager.gameState is GameState.Menu or GameState.Encyclopedia;

    // Every frame; active when Wanted and none of NOVR's own menu screens is up.
    public static void Tick(bool active)
    {
        var cursor = VrUiCursor.I;
        if (cursor == null) return;

        try
        {
            if (!active)
            {
                cursor.SetNavigationClick(false);
                cursor.ReleaseNavigation();
                return;
            }

            if (Pressed(MenuCommand.Up)) cursor.Navigate(Vector2.up);
            if (Pressed(MenuCommand.Down)) cursor.Navigate(Vector2.down);
            if (Pressed(MenuCommand.Left)) cursor.Navigate(Vector2.left);
            if (Pressed(MenuCommand.Right)) cursor.Navigate(Vector2.right);
            if (Pressed(MenuCommand.PreviousTab)) cursor.Page(-1);
            if (Pressed(MenuCommand.NextTab)) cursor.Page(1);
            if (Pressed(MenuCommand.Back)) cursor.NavigateBack();
            cursor.SetNavigationClick(MenuInput.HeldOn(MenuCommand.Select, MenuDevice.Joystick));
        }
        catch (Exception exception)
        {
            cursor.SetNavigationClick(false);
            if (_loggedError) return;

            _loggedError = true;
            NOVRPlugin.Log.LogWarning($"[NOVR] Menu navigation error: {exception}");
        }
    }

    private static bool Pressed(MenuCommand command) => MenuInput.PressedOn(command, MenuDevice.Joystick);
}
