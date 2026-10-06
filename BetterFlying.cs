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

[assembly: MelonInfo(typeof(DynamicFlight.DynamicFlightMod), "Dynamic Flight", "1.2.0", "Astronoz")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace DynamicFlight
{
    public class DynamicFlightMod : MelonMod
    {
        private const string CategoryName = "Dynamic Flight";

        // Prefrences
        private MelonPreferences_Category _prefCat;
        private MelonPreferences_Entry<bool> _prefEnabled;
        private MelonPreferences_Entry<float> _prefMoveSpeed;
        private MelonPreferences_Entry<float> _prefVerticalSpeed;
        private MelonPreferences_Entry<bool> _prefNoclipEnabled;
        private MelonPreferences_Entry<float> _prefSpeedMultiplier;
        private MelonPreferences_Entry<bool> _prefFlipsEnabled;
        private MelonPreferences_Entry<float> _prefSpinSpeed;

        // Flight State
        private bool _isFlying = false;
        private Vector3 _currentVelocity = Vector3.zero;
        private float _lastBPressTime = 0f;
        private float _lastAPressTime = 0f;
        private float _doubleTapDelay = 0.35f;

        // Speed Multiplyer State
        private bool _isSpeedBoosted = false;
        private float _currentSpeedMultiplier = 1f;
        private float _speedBuildUp = 0f;
        private float _speedBuildUpRate = 2f;

        // Trigger Double-tap Tracking
        private float _lastLeftTriggerPressTime = 0f;
        private float _lastRightTriggerPressTime = 0f;
        private bool _leftTriggerPressed = false;
        private bool _rightTriggerPressed = false;
        private bool _speedBoostCooldown = false;
        private float _speedBoostCooldownTimer = 0f;
        private const float SPEED_BOOST_COOLDOWN = 0.5f;

        // Noclip State
        private bool _isNoclip = false;
        private float _lastThumbstickPressTime;
        private List<Collider> _disabledColliders = new List<Collider>();

        // Physics state
        private float[] _savedDrags;
        private bool _physicsModified = false;

        // Camera Rotation State
        private float _currentRoll = 0f;
        private float _currentPitch = 0f;

        // Head Tracking State
        private Quaternion _initialHeadRotation = Quaternion.identity;
        private bool _headTrackingInit = false;

        // Spin State
        private float _rollSpinDir = 1f;
        private float _pitchSpinDir = 1f;

        // Stutter Reduction

        private Vector3 _smoothedVelocity = Vector3.zero;
        private bool _hasHeadRefInit = false;
        private float Damping = 8;

        public override void OnInitializeMelon()
        {
            _prefCat = MelonPreferences.CreateCategory(CategoryName);
            _prefEnabled = _prefCat.CreateEntry("Enabled", true);
            _prefMoveSpeed = _prefCat.CreateEntry("MoveSpeed", 15f);
            _prefVerticalSpeed = _prefCat.CreateEntry("VerticalSpeed", 10f);
            _prefNoclipEnabled = _prefCat.CreateEntry("NoclipEnabled", true);
            _prefSpeedMultiplier = _prefCat.CreateEntry("SpeedMultiplier", 2f);
            _prefFlipsEnabled = _prefCat.CreateEntry("FlipsEnabled", false);
            _prefSpinSpeed = _prefCat.CreateEntry("SpinSpeed", 0.5f);

            try
            {
                SetupBoneMenu();
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"BoneMenu FAILED: {ex}");
            }
            
            LoggerInstance.Msg("DynamicFlight Initialized");
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
            
            // Flips Toggle
            page.CreateBool("Flips", Color.white, _prefFlipsEnabled.Value, (Action<bool>)(val =>
            {
                _prefFlipsEnabled.Value = val;
                _prefCat.SaveToFile(true);
                if (_isFlying)
                {
                    ResetCameraRotation();
                }
            }));

            // Speed
            page.CreateFloat("Speed", Color.blue, _prefMoveSpeed.Value, 5f, 5f, 100f, (Action<float>)(val =>
            {
                _prefMoveSpeed.Value = val;
                _prefCat.SaveToFile(true);
            }));

            // Vertical Speed
            page.CreateFloat("Vertical Speed", Color.blue, _prefVerticalSpeed.Value, 5f, 5f, 100f, (Action<float>)(val =>
            {
                _prefVerticalSpeed.Value = val;
                _prefCat.SaveToFile(true);
            }));

            // Speed Multiplier
            page.CreateFloat("Speed Multiplier", Color.yellow, _prefSpeedMultiplier.Value, 1f, 1f, 5f, (Action<float>)(val =>
            {
                _prefSpeedMultiplier.Value = val;
                _prefCat.SaveToFile(true);
            }));

            // Spin Speed
            page.CreateFloat("Spin Speed", Color.yellow, _prefSpinSpeed.Value, 1f, 1f, 20f, (Action<float>)(val =>
            {
                val = Mathf.Round(val * -10f);
                _prefSpinSpeed.Value = val;
                _prefCat.SaveToFile(true);
            }));
        }
        private void ResetCameraRotation()
        {
            _currentPitch = 0f;
            _currentRoll = 0f;
        }

        public override void OnUpdate()
        {
            // Update Speed Boost Cooldown Timer
            if (_speedBoostCooldown)
            {
                _speedBoostCooldownTimer -= Time.unscaledDeltaTime;
                if (_speedBoostCooldownTimer <= 0f)
                {
                    _speedBoostCooldown = false;
                }
            }
            
            if (!_hasHeadRefInit && Player.HandsExist)
            {
                Transform head = Player.Head;
                if (head != null)
                {
                    _initialHeadRotation = head.rotation;
                    _headTrackingInit = true;
                    _hasHeadRefInit = true;
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

            if (Player.RightController.GetAButtonDown())
            {
                float now = Time.unscaledTime;
                if (now - _lastAPressTime <= _doubleTapDelay)
                {
                    if (_isFlying) ResetCameraRotation();
                    _lastAPressTime = 0f;
                }
                else
                {
                    _lastAPressTime = now;
                }
            }

            if (_isFlying)
            {
                bool leftTriggerDown = GetTriggerPressed(XRNode.LeftHand);
                bool rightTriggerDown = GetTriggerPressed(XRNode.RightHand);

                // Speed Mult Trigger
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

                if (leftTriggerDown && rightTriggerDown && !_speedBoostCooldown)
                {
                    float leftTime = Time.unscaledTime - _lastLeftTriggerPressTime;
                    float rightTime = Time.unscaledTime - _lastRightTriggerPressTime;

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

            // Noclip
            if (_isFlying)
            {
                bool thumbstickPressed = Player.RightController.GetThumbStickDown();
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
            }

            // Ragdoll Implementation
            if (_isFlying)
            {
                var rm = Player.RigManager;
                bool ragdolled = rm.physicsRig.torso.shutdown || !rm.physicsRig.ballLocoEnabled;
                if (ragdolled)
                {
                    StopFlying();
                }

            }

            

            if (_isFlying && _prefFlipsEnabled.Value && _headTrackingInit && Player.RightController.GetAButton())
            {
                ProcessForSpins();
            }
        }

        private void ProcessForSpins()
        {
            float pitchInput = 0f;
            float rollInput = 0f;
            float rightGrip = GetGripValue(XRNode.RightHand);
            float leftGrip = GetGripValue(XRNode.LeftHand);
            float rightTrigger = GetTriggerValue(XRNode.RightHand);
            float leftTrigger = GetTriggerValue(XRNode.LeftHand);
                
            if (Mathf.Abs(rightGrip) > 0.05f) rollInput -= rightGrip;
            if (Mathf.Abs(leftGrip) > 0.05f) rollInput += leftGrip;    
            if (Mathf.Abs(rollInput) > 0.1f)
            {
                _currentRoll += rollInput * _prefSpinSpeed.Value * Time.unscaledDeltaTime;
            }

            if (Mathf.Abs(rightTrigger) > 0.05f) pitchInput -= rightTrigger;
            if (Mathf.Abs(leftTrigger) > 0.05f) pitchInput += leftTrigger;
            if (Mathf.Abs(pitchInput) > 0.1f)
            {
                _currentPitch += pitchInput * _prefSpinSpeed.Value * Time.unscaledDeltaTime;
            }

            if (rightTrigger == leftTrigger)
            {
                _currentPitch += 0f;
            }

            if (rightGrip == leftGrip)
            {
                _currentRoll += 0f;
            }
            
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
        private float GetTriggerValue(XRNode node)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue))
            {
                return triggerValue;
            }
            return 0;
        }
        private float GetGripValue(XRNode node)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.grip, out float gripValue))
            {
                return gripValue;
            }
            return 0;
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

            _currentVelocity = Vector3.Lerp(_currentVelocity, targetVelocity, Time.fixedDeltaTime * Damping * 2f);

            Rigidbody[] bodyParts = GetAllBodyRigidbodies(physicsRig);

            Vector3 finalVelocity = _currentVelocity;
            
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
            if (_isFlying && _prefFlipsEnabled.Value)
            {
                ApplyCameraRotation();
            }
        }
        
        private void ApplyCameraRotation()
        {
            if (!_isFlying || !_prefFlipsEnabled.Value) return;

            var rm = Player.RigManager;
            if (rm == null) return;

            if (Time.timeScale <= 0f) return;

            var openControllerRig = rm.controllerRig.TryCast<OpenControllerRig>();
            if (openControllerRig == null) return;

            Transform playspace = openControllerRig.transform;
            Transform playspaceHead = openControllerRig.m_head.transform;
            Transform physicsHead = rm.physicsRig.m_head;

            if (Mathf.Abs(_currentRoll) > 0.01f || Mathf.Abs(_currentPitch) > 0.01f)
            {
                playspace.localPosition = Vector3.zero;
                playspace.localRotation = Quaternion.identity;

                playspace.rotation = Quaternion.identity;
                playspace.rotation = physicsHead.rotation * Quaternion.Inverse(playspaceHead.rotation);

                Quaternion rollRotation = Quaternion.AngleAxis(_currentRoll, physicsHead.forward);
                Quaternion pitchRotation = Quaternion.AngleAxis(_currentPitch, physicsHead.right);
                playspace.rotation = rollRotation * pitchRotation * playspace.rotation;

                playspace.position += physicsHead.position - playspaceHead.position;
            }
        }
        
        private void UpdateSpeedBoost()
        {
            float targetMultiplier = _isSpeedBoosted ? _prefSpeedMultiplier.Value : 1f;

            if(_isSpeedBoosted)
            {
                _speedBuildUp += Time.fixedDeltaTime * _speedBuildUpRate;
                _speedBuildUp = Mathf.Min(_speedBuildUp, 1f);
                _currentSpeedMultiplier = Mathf.Lerp(1f, targetMultiplier, _speedBuildUp); 
            }
            else
            {
                _speedBuildUp -= Time.fixedDeltaTime * _speedBuildUpRate;
                _speedBuildUp = Mathf.Max(_speedBuildUp, 1f);
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
                    Message = $"Boost On ({_prefSpeedMultiplier.Value}x)",
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
            _currentSpeedMultiplier = 1f;
            _isSpeedBoosted = false;
            _speedBuildUp = 0f;

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
                    PopupLength = 1.5f,
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
                    PopupLength = 1.5f,
                    Type = NotificationType.Error
                });
        }
    }
}