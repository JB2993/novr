using UnityEngine;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRDynamicMapBehavior : MonoBehaviour
{
    private Canvas? _canvas;

    private void Start()
    {
        _canvas = GetComponentInParent<Canvas>();
        if (_canvas != null)
        {
            _canvas.worldCamera = APIBus.CockpitHudCamera;
            VrCanvasHitTester.Register(_canvas);
        }
    }

    private void OnDestroy()
    {
        if (_canvas != null)
            VrCanvasHitTester.Unregister(_canvas);
    }
}
