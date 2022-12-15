using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Android;
using UnityEngine.UI;
using stcalc;

//TYPES 
public enum RotationMode
{
    Gyro,
    Manual,
    Focused
}


public class CameraController : MonoBehaviour
{

    // STATE
    private GameObject _compass;
    private PointerHelper _pointerHelper;
    private float _initialYAngle = 0f;
    private float _appliedGyroYAngle = 0f;
    private float _calibrationYAngle = 0f;
    private Transform _player;
    private float _tempSmoothing;
    private RotationMode _mode = RotationMode.Gyro;
    private RotationMode _savedMode = RotationMode.Gyro;
    private Camera _cam;
    private StarCollider _starCollider;
    private GameObject _FocusedStar;
    private float _deltaSelectionTime = 0;
    Vector2 _hitPoint;
    Vector2 _centerPoint = new Vector2(0.5f, 0.5f);
    private bool _shouldUpdateDateTime = true;
    private bool _isSwitchingRotationMode;

    public UnityEvent<string> onLocationFailed = new UnityEvent<string>();
    public UnityEvent<string> onLocationSucceed = new UnityEvent<string>();

    // SETTINGS
    [SerializeField] private List<GameObject> labels = new List<GameObject>();
    [SerializeField] private GameObject _starsWrapperObj;
    [SerializeField] private float _smoothing = 0.1f;
    [SerializeField] private float _cameraSensitivity = 2f;
    [SerializeField] private float _FOVSensitivity = 15f;
    [SerializeField] private GUIController guiController;

    // Start is called before the first frame update
    private IEnumerator Start()
    {
        Application.targetFrameRate = 60;

        _starsWrapperObj = GameObject.Find("BigBang"); //for debug shorts
        _initialYAngle = transform.eulerAngles.y;
        _cam = Camera.main;
        _cam.fieldOfView = 50;
        _pointerHelper = new PointerHelper();
        _compass = GameObject.Find("Compass");

        Input.compass.enabled = true;
        Input.gyro.enabled = true;

        var colliderObj = new GameObject("Collider");
        colliderObj.transform.parent = transform;
        colliderObj.layer = 2;
        colliderObj.AddComponent<MeshCollider>();
        colliderObj.AddComponent<MeshFilter>();

        _starCollider = colliderObj.AddComponent<StarCollider>();
        _starCollider.maxCount = 50;
        _starCollider.distance = 100;

        StartCoroutine(LocationCoroutine());

        _player = new GameObject("Player").transform;
        _player.position = transform.position;
        _player.rotation = transform.rotation;

        // Wait until gyro is active, then calibrate to reset starting rotation.
        yield return new WaitForSeconds(1);
        StartCoroutine(CalibrateYAngle());
    }

    // Update is called once per frame
    void Update()
    {
        //CONTEXT
        if (_shouldUpdateDateTime) StartCoroutine(SetDateTime());

        //INPUTS
        if (_isSwitchingRotationMode) return;

        if (GetRotationMode() == RotationMode.Focused)
        {
            RotateAroundFocusedObject();
        }
        else
        {
            if (GetRotationMode() == RotationMode.Gyro)
            {
                ApplyGyroRotation();
            }
            if (GetRotationMode() == RotationMode.Manual)
            {
                SetCameraRotation();
            }

            ChangeFOVOnPinch(1, 50);
            SelectStar();

            //BINDINGS
            DisplayNames();
        }
    }


    //CONTEXT
    private IEnumerator SetDateTime()
    {
        _shouldUpdateDateTime = false;

        ContextProvider.dateTimeJd = Moment.GetJulianCurrentDay();
        yield return new WaitForSeconds(0.5f);

        _shouldUpdateDateTime = true;
    }


    //INPUTS
    private IEnumerator CalibrateYAngle()
    {
        _tempSmoothing = _smoothing;
        _smoothing = 1;
        _calibrationYAngle = _appliedGyroYAngle - _initialYAngle; // Offsets the y angle in case it wasn't 0 at edit time.
        yield return null;
        _smoothing = _tempSmoothing;
    }

    private void ApplyGyroRotation()
    {
        _player.rotation = Input.gyro.attitude;
        _player.Rotate(0f, 0f, 180f, Space.Self); // Swap "handedness" of quaternion from gyro.
        _player.Rotate(90f, 180f, 0f, Space.World); // Rotate to make sense as a camera pointing out the back of your device.
        _appliedGyroYAngle = _player.eulerAngles.y; // Save the angle around y axis for use in calibration.

        float sensitivity = ComputeCoords.Remap(_cam.fieldOfView, 30, 1, 1, 0.1f);
        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            _player.rotation,
            _smoothing * sensitivity
        );
    }

    private void ApplyCalibration()
    {
        _player.Rotate(0f, -_calibrationYAngle, 0f, Space.World); // Rotates y angle back however much it deviated when calibrationYAngle was saved.
    }

    private void SetCameraRotation()
    {
        if (_pointerHelper.IsPointerOverUIElement()) return;

        if (Input.touchCount == 1)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Moved)
            {
                var rot = transform.rotation.eulerAngles;
                var sensitivity = _cameraSensitivity / _cam.fieldOfView;

                // clamp values between 90 and 275, player object is rotated
                var clampedRotX = rot.x + touch.deltaPosition.y / sensitivity;
                if (clampedRotX >= 90 && clampedRotX <= 180) clampedRotX = 90;
                if (clampedRotX <= 275 && clampedRotX >= 180) clampedRotX = 275;

                _player.rotation = Quaternion.Euler(clampedRotX, rot.y - touch.deltaPosition.x / sensitivity, rot.z);
            }
        }

        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            _player.rotation,
            _smoothing
        );
    }

    private void RotateAroundFocusedObject()
    {
        var focusedStar = GetFocusedStar();
        transform.position = focusedStar.transform.position - transform.forward * 1.5f * focusedStar.transform.localScale.x;

        if (_pointerHelper.IsPointerOverUIElement()) return;

        if (Input.touchCount == 1)
        {
            var touch = Input.GetTouch(0);
            transform.RotateAround(focusedStar.transform.position, transform.right, -Mathf.Lerp(0, touch.deltaPosition.y, 0.3f));
            transform.RotateAround(focusedStar.transform.position, transform.up, Mathf.Lerp(0, touch.deltaPosition.x, 0.3f));
        }

        ChangeFOVOnPinch(30, 110f);
    }

    public void SwitchToFocusedMode()
    {
        if (_isSwitchingRotationMode) return;

        var focusedStar = GetFocusedStar();
        if (focusedStar == null) return;

        SaveRotationMode(GetRotationMode());

        labels.ForEach(l => l.GetComponent<Text>().text = "");
        _starCollider.forceStopStarCollider = true;
        focusedStar.GetComponent<Renderer>().enabled = true;
        _compass.GetComponent<CanvasGroup>().alpha = 0;
        _cam.farClipPlane = 50;
        StartCoroutine(SwitchToFocusedModeCoroutine(focusedStar));
    }

    private IEnumerator SwitchToFocusedModeCoroutine(GameObject focusedStar)
    {
        _isSwitchingRotationMode = true;

        var startPos = transform.position;
        var startFOV = _cam.fieldOfView;

        for (float a = 0; a < 1; a += Time.deltaTime)
        {
            _cam.fieldOfView = Mathf.Lerp(startFOV, 80, a);

            transform.position = Vector3.Lerp(
                startPos,
                focusedStar.transform.position - transform.forward * 1.5f * focusedStar.transform.localScale.x,
                a
            );

            yield return null;
        }
        _cam.fieldOfView = 80;

        transform.position = focusedStar.transform.position - transform.forward * 1.5f * focusedStar.transform.localScale.x;

        SetRotationMode(RotationMode.Focused);

        _isSwitchingRotationMode = false;
    }

    public void SwitchToGyroOrManualMode()
    {
        if (_isSwitchingRotationMode) return;
        if (GetRotationMode() != RotationMode.Focused) return;

        _starCollider.forceStopStarCollider = false;
        _compass.GetComponent<CanvasGroup>().alpha = 1;
        _cam.farClipPlane = 120;

        StartCoroutine(SwitchToGyroOrManualModeCoroutine());
    }

    private IEnumerator SwitchToGyroOrManualModeCoroutine()
    {
        _isSwitchingRotationMode = true;

        var startForward = transform.forward;
        var startPos = transform.position;
        var startFOV = _cam.fieldOfView;

        for (float a = 0; a < 1; a += Time.deltaTime)
        {
            _cam.fieldOfView = Mathf.Lerp(startFOV, 50, a);

            transform.position = Vector3.Lerp(startPos, Vector3.zero, a);
            transform.forward = Vector3.Lerp(startForward, GetFocusedStar().transform.position, a);

            yield return null;
        }

        transform.position = Vector3.zero;
        transform.forward = GetFocusedStar().transform.position;

        _cam.fieldOfView = 50;

        SetRotationMode(GetSavedMode());


        _isSwitchingRotationMode = false;
    }

    private void ChangeFOVOnPinch(float minFov, float maxFov)
    {
        if (Input.touchCount == 2)
        {
            Touch firstTouch = Input.GetTouch(0);
            Touch sndTouch = Input.GetTouch(1);

            float deltaFOV = Vector2.Distance(
                firstTouch.position,
                sndTouch.position
            ) - Vector2.Distance(
                firstTouch.position - firstTouch.deltaPosition,
                sndTouch.position - sndTouch.deltaPosition
            );

            _cam.fieldOfView -= deltaFOV / _FOVSensitivity;
            _cam.fieldOfView = Mathf.Clamp(_cam.fieldOfView, minFov, maxFov);
        }
    }

    private void SelectStar()
    {
        if (Input.touchCount > 1)
        {
            _deltaSelectionTime = 0;
            return;
        }

        if (Input.touchCount == 1)
        {
            var firstTouch = Input.GetTouch(0);
            _hitPoint = firstTouch.position;

            float deltaCameraRotation = Vector2.Distance(
                firstTouch.position,
                firstTouch.position - firstTouch.deltaPosition
            );

            _deltaSelectionTime = deltaCameraRotation < 0.05 && !_pointerHelper.IsPointerOverUIElement()
                ? _deltaSelectionTime + Time.deltaTime
                : 99
            ;
        }
        else
        {
            if (
                Input.touchCount == 0
                && _deltaSelectionTime < 0.2f
                && _deltaSelectionTime > 0.05f
            )
            {
                var closestStar = GetClosestStar(_hitPoint);
                if (closestStar != null)
                {
                    guiController.ActiveFocus(closestStar);
                    RotateToFocusedStar(closestStar);
                    SetFocusedStar(closestStar);
                }
            }
            _deltaSelectionTime = 0;
        }
    }

    //BINDINGS
    private void DisplayNames()
    {
        var starsSelected = _cam.fieldOfView > 15
        ? _starCollider.principalStars
        : _starCollider.closestStars;

        var starsToDisplay = starsSelected
            .Where((star) => star.GetComponent<Renderer>().enabled)
            .OrderBy((star) => Mathf.Abs(Vector2.Distance(_cam.WorldToViewportPoint(star.transform.position), _centerPoint)))
            .ThenBy((star) => star.GetComponent<StarPositionner>().mag)
            .ToList();

        for (int i = 0; i < labels.Count; i++)
        {
            var label = labels[i];

            var textObj = label.GetComponent<Text>();
            var canvas = label.GetComponent<CanvasGroup>();

            if (starsToDisplay.Count <= i)
            {
                canvas.alpha = 0;
                continue;
            }

            canvas.alpha = 1;
            textObj.text = starsToDisplay[i].name;

            var starPos = _cam.WorldToScreenPoint(starsToDisplay[i].transform.position);
            label.transform.position =
             new Vector3(
                starPos.x,
                starPos.y - ComputeCoords.RemapBalanced(
                        Mathf.Clamp((float)_cam.fieldOfView, 1, 50), 70, 30,
                        new float[][] { new float[] { 1, 8, 80 }, new float[] { 8, 50, 20 } }
                    ),
                starPos.z
            );
        }
    }


    public void SwitchMode()
    {
        SetRotationMode(GetRotationMode() == RotationMode.Gyro ? RotationMode.Manual : RotationMode.Gyro);
        SaveRotationMode(GetRotationMode());
    }

    private GameObject GetClosestStar(Vector2 screenPos, float maxDistance = 300)
    {
        Vector3 minPos = Vector3.positiveInfinity;
        GameObject ClosestStar = null;
        for (int i = 0; i < _starsWrapperObj.transform.childCount; i++)
        {
            var star = _starsWrapperObj.transform.GetChild(i);
            var starPos = _cam.WorldToScreenPoint(star.position);

            var angle = Vector3.Angle(transform.forward, star.position);
            var distance = Vector3.Distance(star.position, transform.position);
            var adj = Mathf.Cos(angle * Mathf.PI / 180f) * distance;
            bool isLookingForward = adj > 0;

            if (
                ContextProvider.principalStarNames.Contains(star.gameObject.name)
                && Mathf.Abs(Vector2.Distance(screenPos, starPos)) < 100
                && isLookingForward
            )
            {
                return star.gameObject;
            }

            if (
                Mathf.Abs(Vector2.Distance(screenPos, starPos)) < Mathf.Abs(Vector2.Distance(screenPos, minPos))
                && star.GetComponent<Renderer>().enabled
                && isLookingForward
            )
            {
                minPos = starPos;
                ClosestStar = star.gameObject;
            }
        }
        return Mathf.Abs(Vector2.Distance(screenPos, minPos)) < maxDistance ? ClosestStar : null;
    }

    public void RotateToFocusedStar(GameObject closestStar)
    {
        if (GetRotationMode() == RotationMode.Manual) StartCoroutine(RotateToFocusedStarCoroutine(closestStar.transform.position));
    }



    public RotationMode GetRotationMode()
    {
        return _mode;
    }
    private void SetRotationMode(RotationMode mode)
    {
        _mode = mode;
    }
    private RotationMode GetSavedMode()
    {
        return _savedMode;
    }
    private void SaveRotationMode(RotationMode mode)
    {
        _savedMode = mode;
    }



    public void SetFocusedStar(GameObject focusedStar)
    {
        _FocusedStar = focusedStar;
    }
    private GameObject GetFocusedStar()
    {
        return _FocusedStar;
    }



    //COROUTINE
    IEnumerator LocationCoroutine()
    {
#if UNITY_ANDROID
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.CoarseLocation))
        {
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionDenied += (permissionName) => onLocationFailed.Invoke("You have to accept location permission to use the application");
            callbacks.PermissionGranted += (permissionName) => StartCoroutine(LocationCoroutine());

            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.CoarseLocation, callbacks);
            yield break;
        }
#endif
        if (!Input.location.isEnabledByUser)
        {
            onLocationFailed.Invoke("Location not enabled, please turn on location.");
            yield break;
        }
        Input.location.Start(500f, 500f);
        // Wait until service initializes
        int maxWait = 5;
        while (Input.location.status == LocationServiceStatus.Initializing && maxWait > 0)
        {
            yield return new WaitForSecondsRealtime(1);
            maxWait--;
        }

        // Service didn't initialize in 15 seconds
        if (maxWait < 1)
        {
            onLocationFailed.Invoke("Location service didn't initialize, please try again.");
            yield break;
        }

        // Connection has failed
        if (Input.location.status != LocationServiceStatus.Running)
        {
            onLocationFailed.Invoke("Unable to determine device location. Failed with status " + Input.location.status);
            yield break;
        }

        ContextProvider.latitude = Input.location.lastData.latitude;
        ContextProvider.longitude = Input.location.lastData.longitude;

        StartCoroutine(
           DataFetcher.GetLocationName(Input.location.lastData.latitude, Input.location.lastData.longitude,
           (location) =>
           {
               onLocationSucceed.Invoke(location);
           }
       ));

        // Stop service if there is no need to query location updates continuously
        Input.location.Stop();
    }

    IEnumerator FadeText(CanvasGroup textCanvas, bool shouldDisplay)
    {
        float startValue = shouldDisplay ? 0 : 1;
        float endValue = shouldDisplay ? 1 : 0;

        for (float a = 0; a < 1; a += Time.deltaTime * 2)
        {
            textCanvas.alpha = Mathf.Lerp(startValue, endValue, a);
            yield return null;
        }

        textCanvas.alpha = endValue;
    }

    IEnumerator RotateToFocusedStarCoroutine(Vector3 lookingPos)
    {
        var lookingRot = Quaternion.LookRotation(lookingPos - transform.position);
        for (float a = 0; a < 1; a += Time.deltaTime * 2)
        {
            _player.rotation = Quaternion.Slerp(_player.rotation, lookingRot, a);
            yield return null;
        }
        _player.rotation = lookingRot;
    }

    // MONO FUNCTIONS
    public void SetEnabled(bool value)
    {
        enabled = true;
        StartCoroutine(CalibrateYAngle());
    }
}
