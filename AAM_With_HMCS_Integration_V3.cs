
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using SaccFlightAndVehicles.KitKatAddons.HMCS;

namespace SaccFlightAndVehicles
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DFUNC_AAM : UdonSharpBehaviour
    {
        [SerializeField] public UdonSharpBehaviour SAVControl;
        public Animator AAMAnimator;
        [Range(0, 2)]
        [Tooltip("0 = Radar, 1 = Heat, 2 = Other. Controls what variable is added to in SaccAirVehicle to count incoming missiles, AND which variable to check for reduced tracking, (MissilesIncomingHeat NumActiveFlares, MissilesIncomingRadar NumActiveChaff, MissilesIncomingOther NumActiveOtherCM)")]
        public int MissileType = 1;
        public int NumAAM = 6;
        [Tooltip("If target is within this angle of the direction the gun is aiming, it is lockable")]
        public float AAMLockAngle = 15;
        [Tooltip("AAM takes this long to lock before it can fire (seconds)")]
        public float AAMLockTime = 1.5f;
        [Tooltip("Heatseekers only: How much faster is locking if the target has afterburner on? (AAMLockTime / value)")]
        public float LockTimeABDivide = 2f;
        [Tooltip("Heatseekers only: If target's engine throttle is 0%, what is the minimum number to divide lock time by, to prevent infinite lock time. (AAMLockTime / value)")]
        public float LockTimeMinDivide = .2f;
        [Tooltip("Minimum time between missile launches")]
        public float AAMLaunchDelay = 0;
        [Tooltip("How long it takes to fully reload from empty in seconds. Can be inaccurate because it can only reload by integers per resupply")]
        public float FullReloadTimeSec = 10;
        [Tooltip("Make enemy aircraft's animator set the 'targeted' trigger?")]
        public bool SendLockWarning = true;
        [Tooltip("Allow user to fire the weapon while the vehicle is on the ground taxiing?")]
        public bool AllowFiringWhenGrounded = false;
        [Tooltip("Allow locking on target with no missiles left. Enable if creating FOX-1/3 missiles, otherwise your last missile will be unusable.")]
        public bool AllowNoAmmoLock = false;
        [Tooltip("GameObject that is enabled by the missile script for 1 second when the missile enters pitbull mode to let the pilot know he no longer has to track the target. Use if creating FOX-3 missiles.")]
        public GameObject PitBullIndicator;
        [Tooltip("Send the boolean(AnimBoolName) true to the animator when selected?")]
        public bool DoAnimBool = false;
        [Tooltip("Animator bool that is true when this function is selected")]
        public string AnimBoolName = "AAMSelected";
        [Tooltip("Animator float that represents how many missiles are left")]
        public string AnimFloatName = "AAMs";
        [Tooltip("Animator trigger that is set when a missile is launched")]
        public string AnimFiredTriggerName = "aamlaunched";
        [Tooltip("Should the boolean stay true if the pilot exits with it selected?")]
        public bool AnimBoolStayTrueOnExit;
        [Tooltip("Make it only possible to lock if the angle you are looking at the back of the enemy plane is less than HighAspectPreventLock (for heatseekers)")]
        public bool HighAspectPreventLock;
        [Tooltip("Angle beyond which aspect is too high to lock")]
        public float HighAspectAngle = 85;
        [Tooltip("Object that is cloned and fired at the enemy")]
        public GameObject AAM;
        public Transform AAMLaunchPoint;
        [Tooltip("Sound that plays when missile is selected, but has no target")]
        public AudioSource AAMIdle;
        [Tooltip("Sound that plays when missile has a target but no lock")]
        public AudioSource AAMTargeting;
        [Tooltip("Sound that plays when missile has a lock on a target")]
        public AudioSource AAMTargetLock;
        [Tooltip("Fired AAMs will be parented to this object, use if you happen to have some kind of moving origin system")]
        public Transform WorldParent;
        private float HighAspectPreventLockAngleDot;

        [UdonSynced, FieldChangeCallback(nameof(AAMFire))] private ushort _AAMFire;
        public ushort AAMFire
        {
            set
            {
                if (value > _AAMFire)   //if _AAMFire is higher locally, it's because a late joiner just took ownership or value was reset, so don't launch
                {
                    LaunchAAM();
                }
                _AAMFire = value;
            }
            get => _AAMFire;
        }

        [UdonSynced, FieldChangeCallback(nameof(sendtargeted))] private bool _SendTargeted;
        public bool sendtargeted
        {
            set
            {
                _SendTargeted = value;

                if (Pilot) return;

                var Target = AAMTargets[AAMTarget];
                if (Target && Target.transform.parent)
                {
                    AAMCurrentTargetSAVControl = Target.transform.parent.GetComponent<SaccAirVehicle>();
                }

                if (AAMCurrentTargetSAVControl != null)
                {
                    AAMCurrentTargetSAVControl.EntityControl.SendEventToExtensions("SFEXT_L_AAMTargeted");
                }
            }
            get => _SendTargeted;
        }

        private float boolToggleTime;
        private bool AnimOn = false;
        [System.NonSerializedAttribute] public SaccEntity EntityControl;
        private bool UseLeftTrigger = false;
        [System.NonSerializedAttribute] public int FullAAMs;
        private int NumAAMTargets;
        private float AAMLockTimer = 0;
        private bool AAMHasTarget = false;
        [System.NonSerializedAttribute] public bool AAMLocked = false;
        private bool TriggerLastFrame;
        private float AAMLastFiredTime = -999;
        private float FullAAMsDivider;
        float TimeSinceSerialization;
        private bool func_active = false;
        private bool Pilot = false;
        [System.NonSerializedAttribute] public bool IsOwner;
        [System.NonSerializedAttribute] public bool InEditor;
        private float reloadspeed;
        private int NumChildrenStart;
        private bool LeftDial = false;
        private int DialPosition = -999;

        private VRCPlayerApi localPlayer;


        public void DFUNC_LeftDial()
        {
            UseLeftTrigger = true;
        }
        public void DFUNC_RightDial()
        {
            UseLeftTrigger = false;
        }

        public void SFEXT_L_EntityStart()
        {
            FullAAMs = NumAAM;
            reloadspeed = FullAAMs / FullReloadTimeSec;
            FullAAMsDivider = 1f / (NumAAM > 0 ? NumAAM : 10000000);
            EntityControl = (SaccEntity)SAVControl.GetProgramVariable(nameof(SaccAirVehicle.EntityControl));
            AAMTargets = EntityControl.AAMTargets;
            NumAAMTargets = AAMTargets.Length;
            CenterOfMass = (Transform)EntityControl.CenterOfMass;
            VehicleTransform = EntityControl.transform;
            OutsideVehicleLayer = (int)SAVControl.GetProgramVariable(nameof(SaccAirVehicle.OutsideVehicleLayer));
            localPlayer = Networking.LocalPlayer;
            InEditor = localPlayer == null;
            HighAspectPreventLockAngleDot = Mathf.Cos(HighAspectAngle * Mathf.Deg2Rad);

            IsOwner = (bool)SAVControl.GetProgramVariable(nameof(SaccAirVehicle.IsOwner));

            if (LockTimeABDivide <= 0)
            {
                LockTimeABDivide = 0.0001f;
            }
            if (LockTimeMinDivide <= 0)
            {
                LockTimeMinDivide = 0.0001f;
            }

            //HUD
            distance_from_head = HUDControl ? (float)HUDControl.GetProgramVariable(nameof(SAV_HUDController.distance_from_head)) : 1.333f;

            FindSelf();

            if (HUDText_AAM_ammo)
            {
                HUDText_AAM_ammo.text = NumAAM.ToString("F0");
            }

            NumChildrenStart = transform.childCount;

            if (!AAM) return;

            int NumToInstantiate = Mathf.Min(FullAAMs, 10);
            for (int i = 0; i < NumToInstantiate; i++)
            {
                InstantiateWeapon();
            }
        }

        private GameObject InstantiateWeapon()
        {
            GameObject NewWeap = Instantiate(AAM);
            NewWeap.transform.SetParent(transform);
            return NewWeap;
        }

        public void SFEXT_O_PilotEnter()
        {
            Pilot = true;
            if (HUDText_AAM_ammo)
            {
                HUDText_AAM_ammo.text = NumAAM.ToString("F0");
            }

            //Make sure SAVeControl.AAMCurrentTargetSAVControl is correct
            var Target = AAMTargets[AAMTarget];
            if (Target && Target.transform.parent)
            {
                AAMCurrentTargetSAVControl = Target.transform.parent.GetComponent<SaccAirVehicle>();
            }

            RequestSerialization();
        }

        public void SFEXT_G_PilotEnter()
        {
            gameObject.SetActive(true); AAMFire = 0;
        }
        public void SFEXT_G_PilotExit()
        {
            gameObject.SetActive(false);
        }
        public void SFEXT_O_PilotExit()
        {
            Pilot = false;
            AAMLockTimer = 0;
            AAMHasTarget = false;
            AAMLocked = false;
            func_active = false;

            if (AAMIdle)
            {
                AAMIdle.gameObject.SetActive(false);
            }

            if (AAMTargeting)
            {
                AAMTargeting.gameObject.SetActive(false);
            }

            if (AAMTargetLock)
            {
                AAMTargetLock.gameObject.SetActive(false);
            }

            if (AAMTargetIndicator)
            {
                AAMTargetIndicator.gameObject.SetActive(false);
                AAMTargetIndicator.localRotation = Quaternion.identity;
            }
        }
        public void SFEXT_P_PassengerEnter()
        {
            if (HUDText_AAM_ammo)
            {
                HUDText_AAM_ammo.text = NumAAM.ToString("F0");
            }
        }

        public void SFEXT_G_Explode()
        {
            NumAAM = FullAAMs;
            UpdateAmmoVisuals();

            if (func_active)
            {
                DFUNC_Deselected();
            }

            if (DoAnimBool && AnimOn)
            {
                SetBoolOff();
            }
        }

        public void SFEXT_G_ReSupply()
        {
            if (NumAAM != FullAAMs)
            {

                //This is a fantastic way to create a race condition lmao. This is horrendous.
                SAVControl.SetProgramVariable(
                    nameof(SaccAirVehicle.ReSupplied),
                    (int)SAVControl.GetProgramVariable(nameof(SaccAirVehicle.ReSupplied)) + 1);
            }

            NumAAM = (int)Mathf.Min(NumAAM + Mathf.Max(Mathf.Floor(reloadspeed), 1), FullAAMs);
            UpdateAmmoVisuals();
        }

        public void UpdateAmmoVisuals()
        {
            if (AAMAnimator)
            {
                AAMAnimator.SetFloat(AnimFloatName, NumAAM * FullAAMsDivider);
            }

            if (HUDText_AAM_ammo)
            {
                HUDText_AAM_ammo.text = NumAAM.ToString("F0");
            }
        }

        public void SFEXT_G_RespawnButton()
        {
            NumAAM = FullAAMs;
            UpdateAmmoVisuals();

            if (!DoAnimBool || !AnimOn) return;

            SetBoolOff();
        }

        public void SFEXT_G_TouchDown()
        {
            AAMLockTimer = 0;
            AAMTargetedTimer = 2;
        }

        public void SFEXT_O_TakeOwnership() { IsOwner = true; }
        public void SFEXT_O_LoseOwnership() { IsOwner = false; }

        public void DFUNC_Selected()
        {
            func_active = true;

            if (AAMTargetIndicator)
            {
                AAMTargetIndicator.gameObject.SetActive(true);
            }

            if (DoAnimBool && !AnimOn)
            {
                SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SetBoolOn));
            }

            if (HMCSMissileLockReticle)
            {
                HMCSMissileLockReticle.SetActive(true);
            }

            if (TargetDirectionArrow)
            {
                TargetDirectionArrow.SetActive(true);
            }
        }
        public void DFUNC_Deselected()
        {
            TriggerLastFrame = true;

            if (AAMIdle)
            {
                AAMIdle.gameObject.SetActive(false);
            }

            if (AAMTargeting)
            {
                AAMTargeting.gameObject.SetActive(false);
            }

            if (AAMTargetLock)
            {
                AAMTargetLock.gameObject.SetActive(false);
            }

            AAMLockTimer = 0;
            AAMHasTarget = false;
            AAMLocked = false;

            if (AAMTargetIndicator)
            {
                AAMTargetIndicator.localRotation = Quaternion.identity;
                AAMTargetIndicator.localScale = Vector3.one;
                AAMTargetIndicator.gameObject.SetActive(false);
            }

            func_active = false;

            if (DoAnimBool && AnimOn)
            {
                SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(SetBoolOff));
            }

            if (HMCSMissileLockReticle)
            {
                HMCSMissileLockReticle.SetActive(false);
            }

            if (TargetDirectionArrow)
            {
                TargetDirectionArrow.SetActive(false);
            }
        }

        void Update()
        {
            if (!func_active) return;

            TimeSinceSerialization += Time.deltaTime;

            float Trigger = Input.GetAxisRaw(
                UseLeftTrigger
                ? "Oculus_CrossPlatform_PrimaryIndexTrigger"
                : "Oculus_CrossPlatform_SecondaryIndexTrigger");

            if (NumAAMTargets > 0)
            {
                if (MissileType == 1)   //heatseekers check engine output of target
                {
                    AAMLocked = false;
                    if (AAMCurrentTargetSAVControl //if target is SaccAirVehicle, adjust lock time based on throttle status
                        ? AAMLockTimer > AAMLockTime / (AAMCurrentTargetSAVControl.AfterburnerOn
                                                            ? LockTimeABDivide
                                                            : Mathf.Clamp(AAMCurrentTargetSAVControl.EngineOutput / AAMCurrentTargetSAVControl.ThrottleAfterburnerPoint, LockTimeMinDivide, 1))
                        : AAMLockTimer > AAMLockTime && AAMHasTarget)   //target is not a SaccAirVehicle
                    {
                        AAMLocked = true;
                    }
                }
                else
                {
                    AAMLocked = AAMLockTimer > AAMLockTime && AAMHasTarget;
                }

                //firing AAM
                if (Trigger > 0.75 || Input.GetKey(KeyCode.Space))
                {
                    if (!TriggerLastFrame)
                    {
                        if (NumAAM > 0 && AAMLocked && Time.time - AAMLastFiredTime > AAMLaunchDelay)
                        {
                            AAMFire++;  //launch AAM using set

                            RequestSerialization();

                            if (NumAAM == 0 && !AllowNoAmmoLock)
                            {
                                AAMLockTimer = 0; AAMLocked = false;
                            }

                            EntityControl.SendEventToExtensions("SFEXT_O_AAMLaunch");
                        }
                    }
                    TriggerLastFrame = true;
                }
                else TriggerLastFrame = false;
            }

            AAMLockSounds();

            Hud();
        }

        private void AAMLockSounds()
        {
            if (AAMIdle)
            {
                AAMIdle.gameObject.SetActive(!AAMLocked && AAMLockTimer <= 0 && (NumAAM > 0 || AllowNoAmmoLock));
            }

            if (AAMTargeting)
            {
                AAMTargeting.gameObject.SetActive(!AAMLocked && AAMLockTimer > 0 && (NumAAM > 0 || AllowNoAmmoLock));
            }

            if (AAMTargetLock)
            {
                AAMTargetLock.gameObject.SetActive(AAMLocked);
            }
        }

        //AAMTargeting
        public UdonSharpBehaviour HUDControl;
        [Tooltip("Max distance an enemy can be targeted at")]
        public float AAMMaxTargetDistance = 6000;
        [System.NonSerialized] public GameObject[] AAMTargets;
        [System.NonSerialized, UdonSynced(UdonSyncMode.None)] public int AAMTarget = 0;
        private int AAMTargetChecker = 0;
        [System.NonSerialized] public Transform CenterOfMass;
        private Transform VehicleTransform;
        private SaccAirVehicle AAMCurrentTargetSAVControl;
        private int OutsideVehicleLayer;
        private Vector3 AAMCurrentTargetDirection;
        private float AAMTargetedTimer = 2;
        private float AAMTargetObscuredDelay;

        //HMCS
        [Header("HMD:")]
        [Tooltip("Linking an HMCSController will allow players to look at the target they would like to lock on to.")]
        [SerializeField] private KitKatHMCSController LinkedHMCSController;

        [Space(10)]
        [SerializeField] private GameObject HMCSMissileLockReticle;
        [Tooltip("This is how far back you can look before it reaches the edge of the missile FOV and caps. Just like in DCS.")]
        [SerializeField] private float MaxHMCSLookAngle = 90;

        [Space(10)]
        [HideInInspector] public Vector3 ClampedLookAngle;
        [SerializeField] private float HMCSSmoothing = 0.15f;
        [SerializeField] private bool DoReticleLock = true;

        [Header("Target Direction Arrow:")]
        [SerializeField] private GameObject TargetDirectionArrow;
        [SerializeField] private bool Arrow3d = false;
        [Tooltip("The arrow gets longer if the angle to the target is higher. These numbers specify at what angle to the target the arrow is at it's min or max size.")]
        [SerializeField] private float ArrowMinAngle = 5;
        [Tooltip("See above.")]
        [SerializeField] private float ArrowMaxAngle = 30;

        private bool HMDVisible = true;


        private void FixedUpdate()  //old AAMTargeting function
        {
            if (!func_active) return;

            //HMCS TargetDirectionArrow
            if (TargetDirectionArrow)
            {
                if (Arrow3d)
                {
                    TargetDirectionArrow.transform.rotation = Quaternion.LookRotation(AAMCurrentTargetDirection, Vector3.up);
                }
                else
                {
                    TargetDirectionArrow.transform.rotation =
                        Quaternion.LookRotation(
                            Vector3.ProjectOnPlane(
                                AAMCurrentTargetDirection,
                                (localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position - TargetDirectionArrow.transform.position).normalized),
                                LinkedHMCSController
                                    ? LinkedHMCSController.transform.up
                                    : VehicleTransform.up);
                }

                TargetDirectionArrow.transform.localScale =
                    new Vector3(
                        1,
                        1,
                        Mathf.Clamp(
                            Vector3.Angle(
                                (TargetDirectionArrow.transform.position - localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position).normalized,
                                AAMCurrentTargetDirection) - ArrowMinAngle,
                            0,
                            ArrowMaxAngle - ArrowMinAngle) / (ArrowMaxAngle - ArrowMinAngle));
            }

            float DeltaTime = Time.fixedDeltaTime;
            var AAMCurrentTargetPosition = AAMTargets[AAMTarget].transform.position;
            Vector3 HudControlPosition = HUDControl ? HUDControl.transform.position : localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;

            //For locking the angle to forward when hmcs is deactivated.
            if (LinkedHMCSController)
            {
                HMDVisible = !(bool)LinkedHMCSController.GetProgramVariable(nameof(KitKatHMCSController.HMDHidden));
            }

            //Player look direction clamping
            if (LinkedHMCSController)
            {
                ClampedLookAngle = LinkedHMCSController.transform.forward;
            }

            if (LinkedHMCSController)
            {
                Quaternion rotatebackby;
                float angle = Vector3.Angle(LinkedHMCSController.transform.forward, VehicleTransform.forward);
                if (angle > MaxHMCSLookAngle)
                {
                    rotatebackby = Quaternion.AngleAxis(angle - MaxHMCSLookAngle, Vector3.Cross(LinkedHMCSController.transform.forward, VehicleTransform.forward));
                    ClampedLookAngle = rotatebackby * LinkedHMCSController.transform.forward;
                }
            }

            //Smoothlook = Vector3.Slerp(Smoothlook, ClampedLookAngle, HMCSSmoothing);
            float AAMCurrentTargetAngle = Vector3.Angle(LinkedHMCSController && HMCSMissileLockReticle && HMDVisible || !DoReticleLock && LinkedHMCSController && HMCSMissileLockReticle ? ClampedLookAngle : VehicleTransform.forward, (AAMCurrentTargetPosition - HudControlPosition));
            //check 1 target per frame to see if it's infront of us and worthy of being our current target
            var TargetChecker = AAMTargets[AAMTargetChecker];
            var TargetCheckerTransform = TargetChecker.transform;
            var TargetCheckerParent = TargetCheckerTransform.parent;

            Vector3 AAMNextTargetDirection = (TargetCheckerTransform.position - HudControlPosition);
            float NextTargetAngle = Vector3.Angle(LinkedHMCSController && HMCSMissileLockReticle && HMDVisible || !DoReticleLock && LinkedHMCSController && HMCSMissileLockReticle ? ClampedLookAngle : VehicleTransform.forward, AAMNextTargetDirection);
            float NextTargetDistance = Vector3.Distance(CenterOfMass.position, TargetCheckerTransform.position);

            if (TargetChecker.activeInHierarchy)
            {
                SaccAirVehicle NextTargetSAVControl = null;

                if (TargetCheckerParent)
                {
                    NextTargetSAVControl = TargetCheckerParent.GetComponent<SaccAirVehicle>();
                }
                //if target SAVontroller is null then it's a dummy target (or hierarchy isn't set up properly)
                if (!NextTargetSAVControl || (!NextTargetSAVControl.Taxiing && !NextTargetSAVControl.EntityControl._dead))
                {
                    RaycastHit hitnext;
                    //raycast to check if it's behind something
                    bool LineOfSightNext = Physics.Raycast(HudControlPosition, AAMNextTargetDirection, out hitnext, 99999999, 133137 /* Default, Water, Environment, and Walkthrough */, QueryTriggerInteraction.Collide);

                    if (LineOfSightNext
                        && hitnext.collider && hitnext.collider.gameObject.layer == OutsideVehicleLayer //did raycast hit an object on the layer planes are on?
                            && NextTargetAngle < AAMLockAngle
                                && NextTargetAngle < AAMCurrentTargetAngle
                                    && NextTargetDistance < AAMMaxTargetDistance
                                        && (!HighAspectPreventLock || !NextTargetSAVControl || Vector3.Dot(NextTargetSAVControl.VehicleTransform.forward, AAMNextTargetDirection.normalized) > HighAspectPreventLockAngleDot)
                                        || (AAMCurrentTargetSAVControl &&//null check
                                                                    (AAMCurrentTargetSAVControl.Taxiing ||//switch target if current target is taxiing
                                                                    (MissileType == 0 && !AAMCurrentTargetSAVControl._EngineOn)))//switch target if heatseeker and current target's engine is off
                                            || !AAMTargets[AAMTarget].activeInHierarchy//switch target if current target is destroyed
                                            )
                    {
                        if (!TriggerLastFrame)
                        {
                            //found new target
                            AAMCurrentTargetAngle = NextTargetAngle;
                            AAMTarget = AAMTargetChecker;
                            AAMCurrentTargetPosition = AAMTargets[AAMTarget].transform.position;
                            AAMCurrentTargetSAVControl = NextTargetSAVControl;
                            AAMLockTimer = 0;
                            AAMTargetedTimer = .99f;    //don't send targeted this frame incase new target is found next frame
                        }
                    }
                }
            }

            //increase target checker ready for next frame
            AAMTargetChecker++;
            if (AAMTargetChecker == AAMTarget && AAMTarget == NumAAMTargets - 1)
            {
                AAMTargetChecker = 0;
            }
            else if (AAMTargetChecker == AAMTarget)
            {
                AAMTargetChecker++;
            }
            else if (AAMTargetChecker > NumAAMTargets - 1)
            {
                AAMTargetChecker = 0;
            }

            //if target is currently in front of plane, lock onto it

            AAMCurrentTargetDirection = AAMCurrentTargetPosition - HudControlPosition;
            float AAMCurrentTargetDistance = AAMCurrentTargetDirection.magnitude;
            //check if target is active, and if it's SaccAirVehicle is null(dummy target), or if it's not null(plane) make sure it's not taxiing or dead.
            //raycast to check if it's behind something
            RaycastHit hitcurrent;
            bool LineOfSightCur = Physics.Raycast(HudControlPosition, AAMCurrentTargetDirection, out hitcurrent, 99999999, 133137 /* Default, Water, Environment, and Walkthrough */, QueryTriggerInteraction.Collide);
            //used to make lock remain for .25 seconds after target is obscured

            if (!LineOfSightCur || (hitcurrent.collider && hitcurrent.collider.gameObject.layer != OutsideVehicleLayer))
            {
                AAMTargetObscuredDelay += DeltaTime;
            }
            else
            {
                AAMTargetObscuredDelay = 0;
            }

            if ((!(bool)SAVControl.GetProgramVariable(nameof(SaccAirVehicle.Taxiing)) || AllowFiringWhenGrounded)
                && (AAMTargetObscuredDelay < .25f)
                && AAMCurrentTargetDistance < AAMMaxTargetDistance
                && AAMTargets[AAMTarget].activeInHierarchy
                && (!AAMCurrentTargetSAVControl
                    || (!AAMCurrentTargetSAVControl.Taxiing
                        && !AAMCurrentTargetSAVControl.EntityControl._dead
                        && (MissileType != 0 || AAMCurrentTargetSAVControl._EngineOn))) //heatseekers cant lock if engine off
                        && (!HighAspectPreventLock
                            || !AAMCurrentTargetSAVControl
                            || Vector3.Dot(
                                LinkedHMCSController
                                    ? LinkedHMCSController.transform.forward
                                    : AAMCurrentTargetSAVControl.VehicleTransform.forward,
                                AAMCurrentTargetDirection.normalized) > HighAspectPreventLockAngleDot))
            {
                if ((AAMTargetObscuredDelay < .25f) && AAMCurrentTargetDistance < AAMMaxTargetDistance)
                {
                    AAMHasTarget = true;

                    if (AAMCurrentTargetAngle < AAMLockAngle && (NumAAM > 0 || AllowNoAmmoLock))
                    {
                        AAMLockTimer += DeltaTime;

                        if (AAMCurrentTargetSAVControl)
                        {
                            //target is a plane, send the 'targeted' event every second to make the target plane play a warning sound in the cockpit.
                            if (SendLockWarning && AAMTargetedTimer > 1)
                            {
                                sendtargeted = !sendtargeted;
                                RequestSerialization();
                                AAMTargetedTimer = 0;
                            }

                            AAMTargetedTimer += DeltaTime;
                        }
                    }
                    else
                    {
                        AAMTargetedTimer = 2f;
                        AAMLockTimer = 0;
                    }
                }
            }
            else
            {
                AAMTargetedTimer = 2f;
                AAMLockTimer = 0;
                AAMHasTarget = false;
            }

        }

        [Space(10)]
        [Header("AAM HMD Reticle settings")]
        [Tooltip("How fast the circle moves to the target after AAM has established a lock.")]
        [SerializeField] private float LockSmoothing = 5;
        [SerializeField] private float ReturnSmoothing = 0.01f;

        private Vector3 ReticleSmoothDirection = Vector3.zero;
        private Vector3 LockedSmoothDirection = Vector3.zero;

        private float t = 0; //This makes the lock circle gravitate toward targets.
        private float r = 0; //This makes it so the circle actually sticks on the target and doesn't lag behind whenever AAM is locked.


        private void LateUpdate()
        {
            if (!HMCSMissileLockReticle) return;

            HMCSMissileLockReticle.SetActive(func_active);

            r = Mathf.Clamp(
                AAMLocked
                    ? r + (Time.deltaTime * ReturnSmoothing)
                    : r - (Time.deltaTime * LockSmoothing),
                0, 1);

            if (AAMLocked)
            {
                t = Mathf.Clamp(t - Time.deltaTime * ReturnSmoothing, 0, 1);
            }

            if (!AAMLocked)
            {
                if (Vector3.Angle(AAMCurrentTargetDirection, ClampedLookAngle.normalized) < AAMLockAngle
                    && AAMCurrentTargetDirection.magnitude < AAMMaxTargetDistance
                    && (NumAAM > 0 || AllowNoAmmoLock)
                    && (!(bool)SAVControl.GetProgramVariable(nameof(SaccAirVehicle.Taxiing)) || AllowFiringWhenGrounded)
                    && (AAMTargetObscuredDelay < .25f)
                    && AAMTargets[AAMTarget].activeInHierarchy
                    && (!AAMCurrentTargetSAVControl || (!AAMCurrentTargetSAVControl.Taxiing
                    && !AAMCurrentTargetSAVControl.EntityControl._dead
                    && (MissileType != 0 || AAMCurrentTargetSAVControl._EngineOn)
                    && (!HighAspectPreventLock
                        || !AAMCurrentTargetSAVControl
                        || Vector3.Dot(LinkedHMCSController
                            ? LinkedHMCSController.transform.forward
                            : AAMCurrentTargetSAVControl.VehicleTransform.forward,
                        AAMCurrentTargetDirection.normalized) > HighAspectPreventLockAngleDot))))
                {
                    t = Mathf.Clamp(Vector3.Angle(AAMCurrentTargetDirection, ClampedLookAngle), 0, AAMLockAngle) / AAMLockAngle;
                }
                else
                {
                    t = Mathf.Clamp(t + Time.deltaTime * LockSmoothing, 0, 1);
                }
            }

            ReticleSmoothDirection = Vector3.Slerp(ReticleSmoothDirection, Vector3.Lerp(AAMCurrentTargetDirection.normalized, HMDVisible || !DoReticleLock ? ClampedLookAngle.normalized : VehicleTransform.forward, t), HMCSSmoothing);
            LockedSmoothDirection = Vector3.Lerp(ReticleSmoothDirection, AAMCurrentTargetDirection, r);

            HMCSMissileLockReticle.transform.position = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position + LockedSmoothDirection;
            HMCSMissileLockReticle.transform.localPosition = HMCSMissileLockReticle.transform.localPosition.normalized * distance_from_head * 740;
            HMCSMissileLockReticle.transform.rotation = Quaternion.LookRotation(localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position - HMCSMissileLockReticle.transform.position).normalized;
        }

        //hud stuff
        [Header("HUD Stuff:")]
        public Text HUDText_AAM_ammo;
        [Tooltip("Hud element to highlight current target")]
        public Transform AAMTargetIndicator;
        private float distance_from_head;

        private void Hud()
        {
            //AAM Target Indicator
            if (!AAMTargetIndicator) return;

            if (!AAMHasTarget)
            {
                AAMTargetIndicator.localScale = Vector3.zero;
                return;
            }

            AAMTargetIndicator.localScale = Vector3.one;

            AAMTargetIndicator.position = (HUDControl
                ? HUDControl.transform.position
                : localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position) + AAMCurrentTargetDirection;

            AAMTargetIndicator.localPosition = AAMTargetIndicator.localPosition.normalized * distance_from_head;

            AAMTargetIndicator.rotation =
                Quaternion.LookRotation(
                AAMLocked
                ? gameObject.transform.position - AAMTargetIndicator.position   //back of mesh is locked version.
                : AAMTargetIndicator.position - gameObject.transform.position,
                LinkedHMCSController
                ? LinkedHMCSController.transform.up
                : gameObject.transform.up);
        }

        public void LaunchAAM()
        {
            AAMLastFiredTime = Time.time;

            NumAAM = Mathf.Max(NumAAM - 1, 0);   //so it doesn't go below 0 when desync occurs

            UpdateAmmoVisuals();

            if (AAMAnimator)
            {
                AAMAnimator.SetTrigger(AnimFiredTriggerName);
            }

            if (!AAM) return;

            GameObject newAAM;
            if (transform.childCount - NumChildrenStart > 0)
            {
                newAAM = transform.GetChild(NumChildrenStart).gameObject;
            }
            else
            {
                //There weren't any missiles left in the object pool so instantiate a new one.
                newAAM = InstantiateWeapon();
            }

            //WorldParent is for OWML floating origin system.
            newAAM.transform.SetParent(WorldParent ? WorldParent : null);

            newAAM.transform.SetPositionAndRotation(
                AAMLaunchPoint.position,
                AAMLaunchPoint.transform.rotation);

            newAAM.SetActive(true);
            newAAM.GetComponent<Rigidbody>().velocity = (Vector3)SAVControl.GetProgramVariable(nameof(SaccAirVehicle.CurrentVel));
        }

        private void FindSelf()
        {
            int x = 0;
            foreach (UdonSharpBehaviour usb in EntityControl.Dial_Functions_R)
            {
                if (this == usb)
                {
                    DialPosition = x;
                    return;
                }
                x++;
            }

            LeftDial = true;

            x = 0;
            foreach (UdonSharpBehaviour usb in EntityControl.Dial_Functions_L)
            {
                if (this == usb)
                {
                    DialPosition = x;
                    return;
                }
                x++;
            }

            DialPosition = -999;
            Debug.LogWarning("DFUNC_AAM: Can't find self in dial functions.");
        }

        public void SetBoolOn()
        {
            boolToggleTime = Time.time;
            AnimOn = true;
            if (!AAMAnimator) return;

            AAMAnimator.SetBool(AnimBoolName, AnimOn);
        }
        public void SetBoolOff()
        {
            boolToggleTime = Time.time;
            AnimOn = false;

            if (!AAMAnimator) return;

            AAMAnimator.SetBool(AnimBoolName, AnimOn);
        }

        public void KeyboardInput()
        {
            if (LeftDial)
            {
                if (EntityControl.LStickSelection == DialPosition)
                {
                    EntityControl.LStickSelection = -1;
                    return;
                }

                EntityControl.LStickSelection = DialPosition;
                return;
            }

            if (EntityControl.RStickSelection == DialPosition)
            {
                EntityControl.RStickSelection = -1;
                return;
            }

            EntityControl.RStickSelection = DialPosition;
        }
    }
}