using UnityEngine;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRGameplayUIBehaviour : UIRenderedCanvasBehavior
{
    // Active in-game canvases; while any is up and the cursor shows, InGameVrUiBar offers placement controls.
    public static int ActiveCount { get; private set; }

    public override void OnEnable()
    {
        base.OnEnable();
        ActiveCount++;
    }

    private void OnDisable()
    {
        ActiveCount--;
    }

    private void Update()
    {
        transform.localScale = Vector3.one * InGameUiPlacement.CanvasScale;
        transform.position = new Vector3(0f, 0f, InGameUiPlacement.Distance);
    }
}
