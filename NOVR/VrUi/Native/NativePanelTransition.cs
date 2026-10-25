using UnityEngine;

namespace NOVR.VrUi.Native;

public sealed class NativePanelTransition : MonoBehaviour
{
    private const float DurationSeconds = 0.14f;
    private const float HiddenOffsetX = 28f;

    private CanvasGroup? _canvasGroup;
    private RectTransform? _rectTransform;
    private Vector2 _shownPosition;
    private Vector2 _hiddenPosition;
    private bool _targetVisible;

    public static void SetVisible(RectTransform target, bool visible, bool instant = false)
    {
        var transition = target.GetComponent<NativePanelTransition>() ??
                         target.gameObject.AddComponent<NativePanelTransition>();
        transition.SetVisible(visible, instant);
    }

    private void Awake()
    {
        Initialize();
    }

    // Once: taken again on a later call, the shown position would be wherever a hide left it, and creep right each time.
    private void Initialize()
    {
        if (_rectTransform != null) return;

        _rectTransform = (RectTransform)transform;
        _canvasGroup = gameObject.GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        _shownPosition = _rectTransform.anchoredPosition;
        _hiddenPosition = _shownPosition + new Vector2(HiddenOffsetX, 0f);
    }

    public void SetVisible(bool visible, bool instant = false)
    {
        Initialize();

        _targetVisible = visible;
        if (visible && !gameObject.activeSelf)
        {
            gameObject.SetActive(true);
        }

        if (_canvasGroup == null || _rectTransform == null) return;

        _canvasGroup.interactable = visible;
        _canvasGroup.blocksRaycasts = visible;

        if (!instant) return;

        _canvasGroup.alpha = visible ? 1f : 0f;
        _rectTransform.anchoredPosition = visible ? _shownPosition : _hiddenPosition;
        gameObject.SetActive(visible);
    }

    private void Update()
    {
        if (_canvasGroup == null || _rectTransform == null) return;

        var targetAlpha = _targetVisible ? 1f : 0f;
        var targetPosition = _targetVisible ? _shownPosition : _hiddenPosition;
        var maxAlphaDelta = Time.unscaledDeltaTime / DurationSeconds;
        var positionT = Mathf.Clamp01(Time.unscaledDeltaTime / DurationSeconds);

        _canvasGroup.alpha = Mathf.MoveTowards(_canvasGroup.alpha, targetAlpha, maxAlphaDelta);
        _rectTransform.anchoredPosition = Vector2.Lerp(_rectTransform.anchoredPosition, targetPosition, positionT);

        if (!_targetVisible && _canvasGroup.alpha <= 0.01f)
        {
            gameObject.SetActive(false);
        }
    }
}
