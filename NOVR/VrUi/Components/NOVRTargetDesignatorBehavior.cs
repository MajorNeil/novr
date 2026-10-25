using UnityEngine;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRTargetDesignatorBehavior : UIRenderedCanvasBehavior
{
    private float _offset = 1000;

    private void Update()
    {
        var uiCam = APIBus.CockpitHudReference;

        // Exactly where the head points: it used to be pushed 10% further off boresight than the head turned.
        transform.rotation = uiCam.transform.rotation;
        transform.position = transform.forward * _offset;
    }
}