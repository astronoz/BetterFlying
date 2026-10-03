using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;
using Il2CppSLZ.Marrow;
using BoneLib;
using BoneLib.BoneMenu;
using BoneLib.Notifications;

[assembly: MelonInfo(typeof(DynamicFlight.DynamicFlightMod), "DynamicFlight", "1.1.0", "Astronoz")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace DynamicFlight
{
    public class DynamicFlightMod : MelonMod
    {
        private const string CategoryName = "DynamicFlight";
        
        // Preferences
        private MelonPreferences_Category _prefCat;
        private MelonPreferences_Entry<bool> _prefEnabled;
        private MelonPreferences_Entry<float> _prefMoveSpeed;
        private MelonPreferences_Entry<float> _prefVerticalSpeed;
        private MelonPreferences_Entry<bool> _prefNoclipEnabled;
        private MelonPreferences_Entry<float> _prefSpeedMultiplier;
        private MelonPreferences_Entry<bool> _prefSonicBoomEnabled;
        private MelonPreferences_Entry<bool> _prefFlipsMode;
        private MelonPreferences_Entry<float> _prefSpinSpeed;
        
        // Flight state
        private bool _isFlying = false;
        private Vector3 _currentVelocity = Vector3.zero;
        private float _lastBPressTime = 0f;
        private float _doubleTapDelay = 0.35f;
        
        // Speed multiplier state
        private bool _isSpeedBoosted = false;
        private float _currentSpeedMultiplier = 1f;
        private float _speedBuildUp = 0f;
        private float _speedBuildUpRate = 2f;
        
        // Trigger double-tap tracking
        private float _lastLeftTriggerPressTime = 0f;
        private float _lastRightTriggerPressTime = 0f;
        private bool _leftTriggerPressed = false;
        private bool _rightTriggerPressed = false;
        private bool _speedBoostCooldown = false;
        private float _speedBoostCooldownTimer = 0f;
        private const float SPEED_BOOST_COOLDOWN = 0.5f;
        
        // Noclip state
        private bool _isNoclip = false;
        private float _lastThumbstickPressTime = 0f;
        private List<Collider> _disabledColliders = new List<Collider>();
        
        // Physics state
        private float[] _savedDrags;
        private bool _physicsModified = false;

        // Sonic boom state
        private float _currentSpeed = 0f;
        private bool _hasTriggeredSonicBoom = false;
        private float _sonicBoomCooldown = 0f;
        private float _sonicBoomCooldownTime = 10f;
        
        // Repeating shockwave state
        private float _repeatingShockwaveTimer = 0f;
        private float _repeatingShockwaveInterval = 1f;
        private bool _isInSonicBoomMode = false;

        // Camera Rotation State
        private float _currentCameraRoll = 0f;
        private float _currentCameraPitch = 0f;

        // Head Tracking State - Automatic 360° Spin
        private Quaternion _initialHeadRotation = Quaternion.identity;
        private bool _headTrackingInitialized = false;
        
        // Roll spin state
        private bool _isRollSpinning = false;
        private float _rollSpinProgress = 0f;
        private float _rollSpinDirection = 1f;
        private float _rollSpinStartAngle = 0f;
        private float _rollSpinDuration = 0.5f;
        
        // Pitch spin state
        private bool _isPitchSpinning = false;
        private float _pitchSpinProgress = 0f;
        private float _pitchSpinDirection = 1f;
        private float _pitchSpinStartAngle = 0f;
        private float _pitchSpinDuration = 0.5f;
        
        // Thresholds
        private const float ROLL_SPIN_THRESHOLD = 45f;
        private const float PITCH_SPIN_THRESHOLD = 60f;
        private const float DAMPING = 8f;

        // Stutter reduction
        private Vector3 _smoothedVelocity = Vector3.zero;
        private bool _isCameraRotating = false;
        private bool _hasInitializedHeadReference = false;

        public override void OnInitializeMelon()
        {
            _prefCat = MelonPreferences.CreateCategory(CategoryName);
            _prefEnabled = _prefCat.CreateEntry("Enabled", true);
            _prefMoveSpeed = _prefCat.CreateEntry("MoveSpeed", 15f);
            _prefVerticalSpeed = _prefCat.CreateEntry("VerticalSpeed", 10f);
            _prefNoclipEnabled = _prefCat.CreateEntry("NoclipEnabled", true);
            _prefSpeedMultiplier = _prefCat.CreateEntry("SpeedMultiplier", 2f);
            _prefSonicBoomEnabled = _prefCat.CreateEntry("SonicBoomEnabled", true);
            _prefFlipsMode = _prefCat.CreateEntry("FlipsMode", false);
            _prefSpinSpeed = _prefCat.CreateEntry("SpinSpeed", 0.5f);

            try
            {
                SetupBoneMenu();
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"BoneMenu FAILED: {ex}");
            }

            LoggerInstance.Msg("DynamicFlight initialized!");
        }

        private void SetupBoneMenu()
        {
            Page page = Menu.CurrentPage.CreatePage("Dynamic Flight", Color.cyan, 0, true);
            
            // Master Toggle
            page.CreateBool("Mod Enabled", Color.green, _prefEnabled.Value, (Action<bool>)(val =>
            {
                _prefEnabled.Value = val;
                _prefCat.SaveToFile(true);
                if (!val && _isFlying) StopFlying();
            }));
            
            // Flips Mode - No notification
            page.CreateBool("Flips", Color.magenta, _prefFlipsMode.Value, (Action<bool>)(val =>
            {
                _prefFlipsMode.Value = val;
                _prefCat.SaveToFile(true);
                if (_isFlying)
                {
                    ResetCameraRotation();
                }
            }));
            
            // Move Speed
            page.CreateFloat("Move Speed", Color.white, _prefMoveSpeed.Value, 5f, 5f, 100f, (Action<float>)(val =>
            {
                _prefMoveSpeed.Value = val;
                _prefCat.SaveToFile(true);
            }));
            
            // Vertical Speed
            page.CreateFloat("Vertical Speed", Color.white, _prefVerticalSpeed.Value, 5f, 5f, 100f, (Action<float>)(val =>
            {
                _prefVerticalSpeed.Value = val;
                _prefCat.SaveToFile(true);
            }));
            
            // Speed Multiplier
            page.CreateFloat("Speed Multiplier", Color.magenta, _prefSpeedMultiplier.Value, 1f, 1f, 5f, (Action<float>)(val =>
            {
                _prefSpeedMultiplier.Value = val;
                _prefCat.SaveToFile(true);
            }));

            // Spin Speed - 0 to 1 with 0.1 step
            page.CreateFloat("Spin Speed", Color.yellow, _prefSpinSpeed.Value, 0.1f, 0f, 1f, (Action<float>)(val =>
            {
                val = Mathf.Round(val * 10f) / 10f;
                _prefSpinSpeed.Value = val;
                _prefCat.SaveToFile(true);
            }));

            // Sonic Boom Toggle
            page.CreateBool("Sonic Boom", Color.cyan, _prefSonicBoomEnabled.Value, (Action<bool>)(val =>
            {
                _prefSonicBoomEnabled.Value = val;
                _prefCat.SaveToFile(true);
                if (!val)
                {
                    _isInSonicBoomMode = false;
                    _repeatingShockwaveTimer = 0f;
                }
            }));

            LoggerInstance.Msg("BoneMenu created!");
        }

        private void ResetCameraRotation()
        {
            _currentCameraRoll = 0f;
            _currentCameraPitch = 0f;
            _rollSpinProgress = 0f;
            _pitchSpinProgress = 0f;
            _isRollSpinning = false;
            _isPitchSpinning = false;
            _isCameraRotating = false;
        }

        public override void OnUpdate()
        {
            SonicBoomShockwave.Tick();

            // Update speed boost cooldown
            if (_speedBoostCooldown)
            {
                _speedBoostCooldownTimer -= Time.unscaledDeltaTime;
                if (_speedBoostCooldownTimer <= 0f)
                {
                    _speedBoostCooldown = false;
                }
            }

            // Initialize head reference ONCE when the game loads and head exists
            if (!_hasInitializedHeadReference && Player.HandsExist)
            {
                Transform head = Player.Head;
                if (head != null)
                {
                    _initialHeadRotation = head.rotation;
                    _headTrackingInitialized = true;
                    _hasInitializedHeadReference = true;
                }
            }

            if (!_prefEnabled.Value || !Player.HandsExist) return;

            if (Player.RightController.GetBButtonDown())
            {
                float now = Time.unscaledTime;
                if (now - _lastBPressTime <= _doubleTapDelay)
                {
                    if (_isFlying) StopFlying();
                    else StartFlying();
                    _lastBPressTime = 0f;
                }
                else
                {
                    _lastBPressTime = now;
                }
            }

            if (_isFlying)
            {
                bool leftTriggerDown = GetTriggerPressed(XRNode.LeftHand);
                bool rightTriggerDown = GetTriggerPressed(XRNode.RightHand);
                
                // Track trigger states for double-tap detection
                if (leftTriggerDown && !_leftTriggerPressed)
                {
                    _lastLeftTriggerPressTime = Time.unscaledTime;
                    _leftTriggerPressed = true;
                }
                else if (!leftTriggerDown)
                {
                    _leftTriggerPressed = false;
                }
                
                if (rightTriggerDown && !_rightTriggerPressed)
                {
                    _lastRightTriggerPressTime = Time.unscaledTime;
                    _rightTriggerPressed = true;
                }
                else if (!rightTriggerDown)
                {
                    _rightTriggerPressed = false;
                }
                
                // Check for double-tap on BOTH triggers
                if (leftTriggerDown && rightTriggerDown && !_speedBoostCooldown)
                {
                    float leftTime = Time.unscaledTime - _lastLeftTriggerPressTime;
                    float rightTime = Time.unscaledTime - _lastRightTriggerPressTime;
                    
                    // Check if both triggers were pressed within the double-tap window
                    if (leftTime <= _doubleTapDelay && rightTime <= _doubleTapDelay)
                    {
                        ToggleSpeedBoost();
                        _speedBoostCooldown = true;
                        _speedBoostCooldownTimer = SPEED_BOOST_COOLDOWN;
                        _lastLeftTriggerPressTime = 0f;
                        _lastRightTriggerPressTime = 0f;
                    }
                }
            }

            // Noclip: Double-tap right thumbstick
            if (_isFlying)
            {
                bool thumbstickPressed = Player.RightController.GetThumbStickDown();
                var rm = Player.RigManager;
                bool ragdolled = rm.physicsRig.torso.shutdown || !rm.physicsRig.ballLocoEnabled;
                if (thumbstickPressed)
                {
                    float now = Time.unscaledTime;
                    if (now - _lastThumbstickPressTime <= _doubleTapDelay)
                    {
                        if (_isNoclip) DisableNoclip();
                        else EnableNoclip();
                        _lastThumbstickPressTime = 0f;
                    }
                    else
                    {
                        _lastThumbstickPressTime = now;
                    }
                }
                if (ragdolled)
                {
                    StopFlying();
                }
            }

            if (_isFlying && _prefFlipsMode.Value && _headTrackingInitialized)
            {
                ProcessHeadTrackingForSpins();
            }
        }

        private void ProcessHeadTrackingForSpins()
        {
            Transform head = Player.Head;
            if (head == null) return;

            if (!_isRollSpinning && !_isPitchSpinning)
            {
                Quaternion relativeRotation = Quaternion.Inverse(_initialHeadRotation) * head.rotation;
                Vector3 eulerAngles = relativeRotation.eulerAngles;
                
                float rawRoll = eulerAngles.z;
                float rawPitch = eulerAngles.x;
                
                if (rawRoll > 180f) rawRoll -= 360f;
                if (rawPitch > 180f) rawPitch -= 360f;

                if (!_isRollSpinning && Mathf.Abs(rawRoll) > ROLL_SPIN_THRESHOLD)
                {
                    _isRollSpinning = true;
                    _isCameraRotating = true;
                    _rollSpinDirection = Mathf.Sign(rawRoll);
                    _rollSpinProgress = 0f;
                    _rollSpinStartAngle = _currentCameraRoll;
                }

                if (!_isPitchSpinning && Mathf.Abs(rawPitch) > PITCH_SPIN_THRESHOLD)
                {
                    _isPitchSpinning = true;
                    _isCameraRotating = true;
                    _pitchSpinDirection = Mathf.Sign(rawPitch);
                    _pitchSpinProgress = 0f;
                    _pitchSpinStartAngle = _currentCameraPitch;
                }
            }

            if (_isRollSpinning)
            {
                float spinSpeed = _prefSpinSpeed.Value * 1f;
                _rollSpinProgress += Time.unscaledDeltaTime * (1f / _rollSpinDuration) * spinSpeed;
                
                if (_rollSpinProgress >= 1f)
                {
                    _rollSpinProgress = 1f;
                    _isRollSpinning = false;
                    _currentCameraRoll = _rollSpinStartAngle;
                    
                    if (!_isPitchSpinning)
                    {
                        _isCameraRotating = false;
                    }
                    CheckForPostSpinTriggers();
                }
                else
                {
                    float easedProgress = EaseInOutCubic(_rollSpinProgress);
                    _currentCameraRoll = _rollSpinStartAngle + (easedProgress * 360f * _rollSpinDirection);
                }
            }

            if (_isPitchSpinning)
            {
                float spinSpeed = _prefSpinSpeed.Value * 1f;
                _pitchSpinProgress += Time.unscaledDeltaTime * (1f / _pitchSpinDuration) * spinSpeed;
                
                if (_pitchSpinProgress >= 1f)
                {
                    _pitchSpinProgress = 1f;
                    _isPitchSpinning = false;
                    _currentCameraPitch = _pitchSpinStartAngle;
                    
                    if (!_isRollSpinning)
                    {
                        _isCameraRotating = false;
                    }
                    CheckForPostSpinTriggers();
                }
                else
                {
                    float easedProgress = EaseInOutCubic(_pitchSpinProgress);
                    _currentCameraPitch = _pitchSpinStartAngle + (easedProgress * 360f * _pitchSpinDirection);
                }
            }
        }

        private void CheckForPostSpinTriggers()
        {
            if (_isRollSpinning || _isPitchSpinning) return;
            
            Transform head = Player.Head;
            if (head == null) return;

            Quaternion relativeRotation = Quaternion.Inverse(_initialHeadRotation) * head.rotation;
            Vector3 eulerAngles = relativeRotation.eulerAngles;
            
            float rawRoll = eulerAngles.z;
            float rawPitch = eulerAngles.x;
            
            if (rawRoll > 180f) rawRoll -= 360f;
            if (rawPitch > 180f) rawPitch -= 360f;

            if (!_isRollSpinning && Mathf.Abs(rawRoll) > ROLL_SPIN_THRESHOLD)
            {
                _isRollSpinning = true;
                _isCameraRotating = true;
                _rollSpinDirection = Mathf.Sign(rawRoll);
                _rollSpinProgress = 0f;
                _rollSpinStartAngle = _currentCameraRoll;
            }

            if (!_isPitchSpinning && Mathf.Abs(rawPitch) > PITCH_SPIN_THRESHOLD)
            {
                _isPitchSpinning = true;
                _isCameraRotating = true;
                _pitchSpinDirection = Mathf.Sign(rawPitch);
                _pitchSpinProgress = 0f;
                _pitchSpinStartAngle = _currentCameraPitch;
            }
        }

        private float EaseInOutCubic(float t)
        {
            return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
        }

        private bool GetTriggerPressed(XRNode node)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue))
            {
                return triggerValue > 0.5f;
            }
            return false;
        }

        public override void OnFixedUpdate()
        {
            if (!_isFlying) return;

            PhysicsRig physicsRig = Player.PhysicsRig;
            if (physicsRig == null) return;

            Transform head = Player.Head;
            Vector3 forward = head.forward;
            Vector3 right = head.right;

            Vector2 leftStick = Player.LeftController.GetThumbStickAxis();
            Vector2 rightStick = Player.RightController.GetThumbStickAxis();

            UpdateSpeedBoost();

            float currentMoveSpeed = _prefMoveSpeed.Value * _currentSpeedMultiplier;
            float currentVerticalSpeed = _prefVerticalSpeed.Value * _currentSpeedMultiplier;

            Vector3 targetVelocity = Vector3.zero;
            
            targetVelocity += forward * leftStick.y * currentMoveSpeed;
            targetVelocity += right * leftStick.x * currentMoveSpeed;
            targetVelocity += Vector3.up * rightStick.y * currentVerticalSpeed;

            _currentVelocity = Vector3.Lerp(_currentVelocity, targetVelocity, Time.fixedDeltaTime * DAMPING * 2f);
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, _currentVelocity, Time.fixedDeltaTime * 10f);

            _currentSpeed = _currentVelocity.magnitude;

            if (_prefSonicBoomEnabled.Value)
            {
                CheckSonicBoom();

                if (_isInSonicBoomMode)
                {
                    _repeatingShockwaveTimer += Time.fixedDeltaTime;
                    if (_repeatingShockwaveTimer >= _repeatingShockwaveInterval)
                    {
                        _repeatingShockwaveTimer = 0f;
                        TriggerRepeatingShockwave();
                    }
                }
            }

            Rigidbody[] bodyParts = GetAllBodyRigidbodies(physicsRig);
            
            Vector3 finalVelocity = _smoothedVelocity;
            
            foreach (Rigidbody rb in bodyParts)
            {
                if (rb != null)
                {
                    rb.velocity = finalVelocity;
                    rb.useGravity = false;
                    rb.angularVelocity = Vector3.zero;
                }
            }
        }

        public override void OnLateUpdate()
        {
            if (_isFlying && _prefFlipsMode.Value && _isCameraRotating)
            {
                ApplyCameraRotation();
            }
        }

        private void ApplyCameraRotation()
        {
            if (!_isFlying || !_prefFlipsMode.Value) return;

            var rm = Player.RigManager;
            if (rm == null) return;

            if (Time.timeScale <= 0f) return;

            var openControllerRig = rm.controllerRig.TryCast<OpenControllerRig>();
            if (openControllerRig == null) return;

            Transform playspace = openControllerRig.transform;
            Transform playspaceHead = openControllerRig.m_head.transform;
            Transform physicsHead = rm.physicsRig.m_head;

            if (Mathf.Abs(_currentCameraRoll) > 0.01f || Mathf.Abs(_currentCameraPitch) > 0.01f)
            {
                playspace.localPosition = Vector3.zero;
                playspace.localRotation = Quaternion.identity;

                playspace.rotation = Quaternion.identity;
                playspace.rotation = physicsHead.rotation * Quaternion.Inverse(playspaceHead.rotation);

                Quaternion rollRotation = Quaternion.AngleAxis(_currentCameraRoll, physicsHead.forward);
                Quaternion pitchRotation = Quaternion.AngleAxis(_currentCameraPitch, physicsHead.right);
                playspace.rotation = rollRotation * pitchRotation * playspace.rotation;

                playspace.position += physicsHead.position - playspaceHead.position;
            }
        }

        private void CheckSonicBoom()
        {
            if (_sonicBoomCooldown > 0f)
                _sonicBoomCooldown -= Time.fixedDeltaTime;

            const float SONIC_BOOM_SPEED = 100f;

            if (_currentSpeed >= SONIC_BOOM_SPEED && _sonicBoomCooldown <= 0f && !_hasTriggeredSonicBoom)
            {
                _hasTriggeredSonicBoom = true;
                _sonicBoomCooldown = _sonicBoomCooldownTime;
                _isInSonicBoomMode = true;
                _repeatingShockwaveTimer = 0f;
                TriggerSonicBoom();
            }
            else if (_currentSpeed < SONIC_BOOM_SPEED && _isInSonicBoomMode)
            {
                _isInSonicBoomMode = false;
                _repeatingShockwaveTimer = 0f;
            }
            else if (_currentSpeed < SONIC_BOOM_SPEED)
            {
                _hasTriggeredSonicBoom = false;
            }
        }

        private void TriggerSonicBoom()
        {
            Transform head = Player.Head;
            if (head == null) return;

            Vector3 position = head.position;
            float radius = Mathf.Clamp(_currentSpeed / 10f, 5f, 30f) * 9f;
            SonicBoomShockwave.Spawn(position, radius);
            ApplyShockwaveForce(position, radius);
        }

        private void TriggerRepeatingShockwave()
        {
            Transform head = Player.Head;
            if (head == null) return;

            Vector3 position = head.position;
            float radius = Mathf.Clamp(_currentSpeed / 12f, 4f, 25f) * 9f;
            SonicBoomShockwave.Spawn(position, radius);
            ApplyShockwaveForce(position, radius);
        }

        private void ApplyShockwaveForce(Vector3 position, float radius)
        {
            float force = Mathf.Clamp(_currentSpeed * 0.5f, 50f, 500f);
            
            Collider[] colliders = Physics.OverlapSphere(position, radius);
            HashSet<int> processedObjects = new HashSet<int>();

            foreach (Collider col in colliders)
            {
                if (col == null || col.isTrigger) continue;

                Rigidbody rb = col.GetComponentInParent<Rigidbody>();
                if (rb != null && !rb.isKinematic && processedObjects.Add(rb.GetInstanceID()))
                {
                    float distance = Vector3.Distance(position, rb.position);
                    float falloff = 1f - Mathf.Clamp01(distance / radius);
                    float finalForce = force * falloff * 2f;
                    
                    Vector3 explosionDir = (rb.position - position).normalized;
                    rb.AddForce(explosionDir * finalForce, ForceMode.Impulse);
                }
            }
        }

        private void UpdateSpeedBoost()
        {
            float targetMultiplier = _isSpeedBoosted ? _prefSpeedMultiplier.Value : 1f;
            
            if (_isSpeedBoosted)
            {
                _speedBuildUp += Time.fixedDeltaTime * _speedBuildUpRate;
                _speedBuildUp = Mathf.Min(_speedBuildUp, 1f);
                _currentSpeedMultiplier = Mathf.Lerp(1f, targetMultiplier, _speedBuildUp);
            }
            else
            {
                _speedBuildUp -= Time.fixedDeltaTime * _speedBuildUpRate * 1.5f;
                _speedBuildUp = Mathf.Max(_speedBuildUp, 0f);
                _currentSpeedMultiplier = Mathf.Lerp(1f, targetMultiplier, _speedBuildUp);
            }
            
            _currentSpeedMultiplier = Mathf.Clamp(_currentSpeedMultiplier, 1f, _prefSpeedMultiplier.Value);
        }

        private void ToggleSpeedBoost()
        {
            _isSpeedBoosted = !_isSpeedBoosted;
            
            if (_isSpeedBoosted)
            {
                Notifier.Send(new Notification
                {
                    Title = "Speed Boost",
                    Message = $"Boost ON ({_prefSpeedMultiplier.Value}x)",
                    ShowTitleOnPopup = true,
                    PopupLength = 1.5f,
                    Type = NotificationType.Success
                });
            }
            else
            {
                Notifier.Send(new Notification
                {
                    Title = "Speed Boost",
                    Message = "Boost OFF",
                    ShowTitleOnPopup = true,
                    PopupLength = 1.5f,
                    Type = NotificationType.Error
                });
            }
        }

        private Rigidbody[] GetAllBodyRigidbodies(PhysicsRig physRig)
        {
            List<Rigidbody> list = new List<Rigidbody>();
            
            if (physRig.torso != null)
            {
                AddIfNotNull(list, physRig.torso.rbPelvis);
                AddIfNotNull(list, physRig.torso.rbSpine);
                AddIfNotNull(list, physRig.torso.rbChest);
                AddIfNotNull(list, physRig.torso.rbNeck);
                AddIfNotNull(list, physRig.torso.rbHead);
            }
            
            if (physRig.softbody != null)
            {
                AddIfNotNull(list, physRig.softbody.rbArmUpperLf);
                AddIfNotNull(list, physRig.softbody.rbArmUpperRt);
                AddIfNotNull(list, physRig.softbody.rbForearmLf);
                AddIfNotNull(list, physRig.softbody.rbForearmRt);
                AddIfNotNull(list, physRig.softbody.rbSoftHandLf);
                AddIfNotNull(list, physRig.softbody.rbSoftHandRt);
            }
            
            AddIfNotNull(list, physRig.rbKnee);
            AddIfNotNull(list, physRig.rbFeet);
            
            return list.ToArray();
        }

        private void AddIfNotNull(List<Rigidbody> list, Rigidbody rb)
        {
            if (rb != null) list.Add(rb);
        }

        private void StartFlying()
        {
            PhysicsRig physicsRig = Player.PhysicsRig;
            if (physicsRig == null) return;

            _isFlying = true;
            _currentVelocity = Vector3.zero;
            _smoothedVelocity = Vector3.zero;
            _currentSpeedMultiplier = 1f;
            _isSpeedBoosted = false;
            _speedBuildUp = 0f;
            _hasTriggeredSonicBoom = false;
            _sonicBoomCooldown = 0f;
            _isInSonicBoomMode = false;
            _repeatingShockwaveTimer = 0f;
            
            ResetCameraRotation();

            Rigidbody[] bodyParts = GetAllBodyRigidbodies(physicsRig);
            _savedDrags = new float[bodyParts.Length];
            for (int i = 0; i < bodyParts.Length; i++)
            {
                _savedDrags[i] = bodyParts[i].drag;
                bodyParts[i].useGravity = false;
                bodyParts[i].drag = 0f;
            }
            _physicsModified = true;

            Notifier.Send(new Notification
            {
                Title = "Flight",
                Message = "ON",
                ShowTitleOnPopup = true,
                PopupLength = 1.5f,
                Type = NotificationType.Success
            });
        }

        private void StopFlying()
        {
            _isFlying = false;
            _isSpeedBoosted = false;
            _currentSpeedMultiplier = 1f;
            _speedBuildUp = 0f;
            _hasTriggeredSonicBoom = false;
            _isInSonicBoomMode = false;
            _repeatingShockwaveTimer = 0f;
            
            ResetCameraRotation();
            
            if (_isNoclip) DisableNoclip();
            
            RestorePhysics();

            Notifier.Send(new Notification
            {
                Title = "Flight",
                Message = "OFF",
                ShowTitleOnPopup = true,
                PopupLength = 1.5f,
                Type = NotificationType.Error
            });
        }

        private void RestorePhysics()
        {
            if (!_physicsModified) return;

            PhysicsRig physicsRig = Player.PhysicsRig;
            if (physicsRig == null) return;

            Rigidbody[] bodyParts = GetAllBodyRigidbodies(physicsRig);
            for (int i = 0; i < bodyParts.Length && i < _savedDrags.Length; i++)
            {
                bodyParts[i].useGravity = true;
                bodyParts[i].drag = _savedDrags[i];
            }

            _physicsModified = false;
        }

        private void EnableNoclip()
        {
            PhysicsRig physicsRig = Player.PhysicsRig;
            if (physicsRig == null) return;

            _disabledColliders.Clear();
            Collider[] colliders = physicsRig.gameObject.GetComponentsInChildren<Collider>(true);
            foreach (Collider col in colliders)
            {
                if (col != null && col.enabled)
                {
                    col.enabled = false;
                    _disabledColliders.Add(col);
                }
            }
            _isNoclip = true;

            Notifier.Send(new Notification
            {
                Title = "Noclip",
                Message = "ON",
                ShowTitleOnPopup = true,
                PopupLength = 1.0f,
                Type = NotificationType.Success
            });
        }

        private void DisableNoclip()
        {
            foreach (Collider col in _disabledColliders)
            {
                if (col != null) col.enabled = true;
            }
            _disabledColliders.Clear();
            _isNoclip = false;

            Notifier.Send(new Notification
            {
                Title = "Noclip",
                Message = "OFF",
                ShowTitleOnPopup = true,
                PopupLength = 1.0f,
                Type = NotificationType.Error
            });
        }
    }

    public static class SonicBoomShockwave
    {
        private static GameObject _obj = null;
        private static float _timer = 0f;
        private static float _scale = 0f;
        private static float _targetRadius = 8f;

        public static void Spawn(Vector3 pos, float radius)
        {
            if (_obj != null)
            {
                GameObject.Destroy(_obj);
                _obj = null;
            }

            _obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _obj.name = "SonicBoom_Shockwave";
            _obj.transform.position = pos;
            _obj.transform.localScale = Vector3.one * 0.1f;
            
            Collider col = _obj.GetComponent<Collider>();
            if (col != null) GameObject.Destroy(col);
            
            MeshRenderer renderer = _obj.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                Material mat = new Material(Shader.Find("Sprites/Default"));
                mat.color = new Color(0.6f, 0.8f, 1f, 0.4f);
                mat.SetFloat("_Mode", 3f);
                mat.SetInt("_SrcBlend", 5);
                mat.SetInt("_DstBlend", 10);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = 3000;
                renderer.material = mat;
            }
            
            _scale = 0.1f;
            _timer = 0.3f;
            _targetRadius = radius;
        }

        public static void Tick()
        {
            if (_timer <= 0f) return;
            
            _timer -= Time.deltaTime;
            _scale += Time.deltaTime * _targetRadius * 3f;
            
            if (_obj != null)
            {
                _obj.transform.localScale = Vector3.one * _scale;
                
                MeshRenderer renderer = _obj.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    float alpha = Mathf.Clamp01(_timer / 0.3f) * 0.4f;
                    renderer.material.color = new Color(0.6f, 0.8f, 1f, alpha);
                }
            }
            
            if (_timer <= 0f && _obj != null)
            {
                GameObject.Destroy(_obj);
                _obj = null;
            }
        }
    }
}