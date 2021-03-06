﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using UnityEngine;

namespace KAS
{
    public class KASModuleGrab : KASModuleAttachCore
    {
        //Part.cfg
        [KSPField] public Vector3 evaPartPos = new Vector3(0f, 0f, 0f);
        [KSPField] public Vector3 evaPartDir = new Vector3(0f, 0f, -1f);
        [KSPField] public string attachNodeName = null;
        [KSPField] public bool customGroundPos = false;
        [KSPField] public Vector3 dropPartPos = new Vector3(0f, 0f, 0f);
        [KSPField] public Vector3 dropPartRot = new Vector3(0f, 0f, 0f);
        [KSPField] public bool addPartMass = true;
        [KSPField] public bool physicJoint = false;
        [KSPField] public string evaTransformName = "jetpackCollider";
        [KSPField] public bool storable = false;
        [KSPField] public bool stateless = true;
        [KSPField] public int storedSize = 1;
        [KSPField] public string bayType = null;
        [KSPField] public string bayNode  = "top";
        [KSPField] public Vector3 bayRot  = new Vector3(0f, 0f, 0f);

        [KSPField] public float attachMaxDist = 2f;
        [KSPField] public bool attachOnPart = false;
        [KSPField] public bool attachOnEva = false;
        [KSPField] public bool attachOnStatic = false;
        [KSPField] public bool attachSendMsgOnly = false;

        //Sounds
        [KSPField] public string attachPartSndPath = "KAS/Sounds/attach";
        [KSPField] public string attachStaticSndPath = "KAS/Sounds/grappleAttachStatic";
        [KSPField] public string detachSndPath = "KAS/Sounds/detach";

        public FXGroup fxSndAttachPart, fxSndAttachStatic, fxSndDetach;


        //Sounds
        [KSPField] public string grabSndPath = "KAS/Sounds/grab";
        public FXGroup fxSndGrab;

        //Grab
        [KSPField(isPersistant = true)] public bool grabbed = false;
        [KSPField(isPersistant = true)] public string evaHolderVesselName = null;
        public Part evaHolderPart = null;
        private float orgKerbalMass;
        private Collider evaCollider;
        private Transform evaNodeTransform;
        private AttachNode partNode;
        private FixedJoint evaJoint;
        private List<Collider> keepTriggers;
        private bool grab_pending = false;
        private bool syncGrab = false;

        public override string GetInfo()
        {
            var sb = new StringBuilder();

            if (storable)
            {
                sb.AppendFormat("<b>Stored size</b>: {0:F0}", storedSize); sb.AppendLine();
            }
            else
            {
                sb.AppendLine("Part cannot be stored.");
            }

            if (attachOnPart || attachOnEva || attachOnStatic)
            {
                sb.AppendLine("Can attach to:");
                if (attachOnPart)
                {
                    sb.AppendLine("- Parts");
                }
                if (attachOnEva)
                {
                    sb.AppendLine("- EVA");
                }
                if (attachOnStatic)
                {
                    sb.AppendLine("- Ground");
                }
            }

            return sb.ToString();
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (state == StartState.Editor || state == StartState.None) return;
            KAS_Shared.createFXSound(this.part, fxSndGrab, grabSndPath, false);
            KAS_Shared.createFXSound(this.part, fxSndAttachPart, attachPartSndPath, false);
            KAS_Shared.createFXSound(this.part, fxSndDetach, detachSndPath, false);
            KAS_Shared.createFXSound(this.part, fxSndAttachStatic, attachStaticSndPath, false);
            RefreshContextMenu();

            //Get attach node
            if (attachNodeName == null || attachNodeName == "")
            {
                if (this.part.srfAttachNode == null)
                {
                    KAS_Shared.DebugError("Grab - surface attach node cannot be found on the part !");
                    return;
                }
                KAS_Shared.AddNodeTransform(this.part, this.part.srfAttachNode);
                partNode = this.part.srfAttachNode;
            }
            else
            {
                AttachNode an = this.part.findAttachNode(attachNodeName);
                if (an == null)
                {
                    KAS_Shared.DebugError("Grab - " + attachNodeName + " node cannot be found on the part !");
                    return;
                }
                KAS_Shared.AddNodeTransform(this.part, an);
                partNode = an;
            }
        }

        void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> fromToAction)
        {
            if (grabbed && fromToAction.from.vessel == evaHolderPart.vessel)
            {
                KAS_Shared.DebugLog(fromToAction.from.vessel.vesselName + " boarding " + fromToAction.to.vessel.vesselName + " with a part grabbed, dropping it to avoid destruction");
                Drop();
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            if (grabbed)
            {
                GameEvents.onCrewBoardVessel.Remove(new EventData<GameEvents.FromToAction<Part, Part>>.OnEvent(this.OnCrewBoardVessel));
            }
        }

        public override void OnPartUnpack()
        {
            base.OnPartUnpack();

            if (grabbed)
            {
                if (!evaHolderPart)
                {
                    if (evaHolderVesselName != null && evaHolderVesselName != "")
                    {
                        Vessel vess = KAS_Shared.GetVesselByName(evaHolderVesselName);
                        if (vess)
                        {
                            KAS_Shared.DebugLog("OnPartUnpack(EvaGrab) - Re-set grab after load on : " + evaHolderVesselName);
                            Grab(vess);
                        }
                        else
                        {
                            GameEvents.onCrewBoardVessel.Remove(new EventData<GameEvents.FromToAction<Part, Part>>.OnEvent(this.OnCrewBoardVessel));
                            evaHolderVesselName = null;
                            evaHolderPart = null;
                            grabbed = false;
                        }
                    }
                }
                else if (grab_pending)
                {
                    grabbed = false;
                    Grab(this.vessel);
                    grab_pending = false;
                }
            }
        }

        internal bool GrabPending()
        {
            KAS_Shared.DebugLog("GrabPending - Preparing To Grab part :" + this.part.partInfo.name);
            evaHolderVesselName = this.vessel.vesselName;
            evaHolderPart = this.vessel.rootPart;
            grabbed = grab_pending = true;
            return true;
        }

        private void SetEvaNode(Vessel kerbalEvaVessel)
        {
            //Get eva transform
            evaCollider = KAS_Shared.GetEvaCollider(kerbalEvaVessel, evaTransformName);
            if (evaNodeTransform) Destroy(evaNodeTransform.gameObject);
            evaNodeTransform = new GameObject("KASEvaNode").transform;
            evaNodeTransform.parent = evaCollider.transform;
            evaNodeTransform.localPosition = evaPartPos;
            evaNodeTransform.rotation = KAS_Shared.DirectionToQuaternion(evaCollider.transform, evaPartDir);
        }

        private void DisableColliders()
        {
            keepTriggers = new List<Collider>();
            List<Collider> allColliders = new List<Collider>(this.part.GetComponentsInChildren<Collider>() as Collider[]);
            foreach (Collider col in allColliders)
            {
                if (col.isTrigger)
                {
                    keepTriggers.Add(col);
                }

                col.isTrigger = true;
            }
        }

        public void FixedUpdate()
        {
            if (syncGrab)
            {
                KAS_Shared.MoveAlign(this.part.transform, partNode.nodeTransform, evaNodeTransform);
                this.part.rigidbody.velocity = FlightGlobals.ActiveVessel.rootPart.rigidbody.velocity;
            }
        }

        public void Grab(Vessel kerbalEvaVessel)
        {
            //Validation
            KASModuleGrab grabbedPartModule = KAS_Shared.GetGrabbedPartModule(kerbalEvaVessel);
            if (grabbedPartModule)
            {
                if (grabbedPartModule.part.packed)
                {
                    KAS_Shared.DebugWarning("Grab - cannot drop the current grabbed part ! (packed)");
                    return;
                }
            }
            //Start grab 
            StartCoroutine(GrabCoroutine(kerbalEvaVessel));
        }

        private IEnumerator GrabCoroutine(Vessel kerbalEvaVessel)
        {
            KAS_Shared.DebugLog("Grab - Grabbing part :" + this.part.partInfo.name);
            //Send message to other modules
            base.SendMessage("OnPartGrab", kerbalEvaVessel, SendMessageOptions.DontRequireReceiver);

            //Set attach node on EVA
            SetEvaNode(kerbalEvaVessel);

            //Drop grabbed eva part if any
            KASModuleGrab grabbbedPartModule = KAS_Shared.GetGrabbedPartModule(kerbalEvaVessel);
            if (grabbbedPartModule)
            {
                grabbbedPartModule.Drop();
            }

            //Unplug winch connected head if any
            KASModuleWinch moduleWinch = KAS_Shared.GetConnectedWinch(this.part);
            if (moduleWinch)
            {
                moduleWinch.UnplugHead(false);
            }

            //Disable all colliders on part
            DisableColliders();

            //Detach if needed
            Detach();

            //Decouple part (if not already done, in case of loading after a save with the part grabbed)
            if (this.part.vessel != kerbalEvaVessel)
            {
                KAS_Shared.DecoupleFromAll(this.part);
            }

            //Wait decouple action (x64 fix)
            yield return new WaitForFixedUpdate();

            //Move part to eva node
            KAS_Shared.MoveAlign(this.part.transform, partNode.nodeTransform, evaNodeTransform);

            //Grab winch connected head if any
            if (moduleWinch)
            {
                KASModulePort modulePort = this.part.GetComponent<KASModulePort>();
                moduleWinch.GrabHead(kerbalEvaVessel, modulePort);
            }

            //Couple part to eva (if not already done, in case of loading after a save with the part grabbed)
            if (this.part.vessel != kerbalEvaVessel)
            {
                this.part.Couple(kerbalEvaVessel.rootPart);
            }

            //Destroy joint to avoid buggy eva move
            if (this.part.attachJoint)
            {
                this.part.attachJoint.DestroyJoint();
            }

            //Set part to physic join or kinematic with parent
            if (physicJoint)
            {
                if (evaJoint) Destroy(evaJoint);
                evaJoint = this.part.gameObject.AddComponent<FixedJoint>();
                evaJoint.connectedBody = evaCollider.attachedRigidbody;
                evaJoint.breakForce = 5;
                evaJoint.breakTorque = 5;
                KAS_Shared.ResetCollisionEnhancer(this.part);
            }
            else
            {
                syncGrab = true;
                KAS_Shared.ResetCollisionEnhancer(this.part, false);
            }
            
            //Add grabbed part mass to eva
            if (addPartMass && !physicJoint)
            {
                orgKerbalMass = kerbalEvaVessel.rootPart.mass;
                kerbalEvaVessel.rootPart.mass += this.part.mass;
            }

            //Add event
            GameEvents.onCrewBoardVessel.Add(new EventData<GameEvents.FromToAction<Part, Part>>.OnEvent(this.OnCrewBoardVessel));

            //Set variables
            evaHolderVesselName = kerbalEvaVessel.vesselName;
            evaHolderPart = kerbalEvaVessel.rootPart;
            grabbed = true;
            grab_pending = false;

            //Refresh Context Menu
            RefreshContextMenu();

            //Play grab sound
            fxSndGrab.audio.Play();

            //Send message to other modules
            base.SendMessage("OnPartGrabbed", kerbalEvaVessel, SendMessageOptions.DontRequireReceiver);
        }

        public void Drop(bool forAttach = false)
        {
            if (grabbed)
            {
                KAS_Shared.DebugLog("Drop - Dropping part :" + this.part.partInfo.name);

                base.SendMessage("OnPartDrop", SendMessageOptions.DontRequireReceiver);

                if (this.part.vessel.isEVA || grab_pending)
                {
                    this.part.decouple();
                }

                if (grab_pending)
                {
                    Destroy(evaNodeTransform.gameObject);
                    evaNodeTransform = null;
                    evaHolderVesselName = null;
                    evaHolderPart = null;
                    grabbed = grab_pending = false;
                    RefreshContextMenu();
                    return;
                }

                //Remove created joints between eva and part if exist
                KAS_Shared.RemoveFixedJointBetween(this.part, evaHolderPart);
                KAS_Shared.RemoveHingeJointBetween(this.part, evaHolderPart);

                List<Collider> allColliders = new List<Collider>(this.part.GetComponentsInChildren<Collider>() as Collider[]);
                foreach (Collider col in allColliders)
                {
                    col.isTrigger = (keepTriggers != null && keepTriggers.Contains(col));
                }

                if (customGroundPos && evaHolderPart.checkLanded())
                {
                    KAS_Shared.MoveRelatedTo(this.part.transform, evaCollider.transform, dropPartPos, dropPartRot);
                }
                else
                {
                    KAS_Shared.MoveAlign(this.part.transform, partNode.nodeTransform, evaNodeTransform);
                }

                if (evaNodeTransform) Destroy(evaNodeTransform.gameObject);
                if (evaJoint) Destroy(evaJoint);

                this.part.rigidbody.velocity = evaHolderPart.rigidbody.velocity;
                this.part.rigidbody.angularVelocity = evaHolderPart.rigidbody.angularVelocity;

                KAS_Shared.ResetCollisionEnhancer(this.part);

                if (addPartMass & !physicJoint) evaHolderPart.mass = orgKerbalMass;

                KASModuleWinch grabbedWinchHead = KAS_Shared.GetWinchModuleGrabbed(evaHolderPart.vessel);
                if (grabbedWinchHead)
                {
                    if (grabbedWinchHead.grabbedPortModule && grabbedWinchHead.grabbedPortModule.part == part)
                    {
                        KAS_Shared.DebugLog("Drop - Grabbed part have a port connected");

                        if (forAttach)
                        {
                            // Docked causes big problems when the part is later coupled
                            grabbedWinchHead.PlugHead(grabbedWinchHead.grabbedPortModule, KASModuleWinch.PlugState.PlugUndocked,fireSound:false);
                        }
                        else
                        {
                            grabbedWinchHead.PlugHead(grabbedWinchHead.grabbedPortModule, KASModuleWinch.PlugState.PlugDocked,fireSound:false);
                        }
                    }
                }

                GameEvents.onCrewBoardVessel.Remove(new EventData<GameEvents.FromToAction<Part, Part>>.OnEvent(this.OnCrewBoardVessel));

                syncGrab = false;
                keepTriggers = null;
                evaJoint = null;
                evaNodeTransform = null;
                evaHolderVesselName = null;
                evaHolderPart = null;
                grabbed = grab_pending = false;

                RefreshContextMenu();

                //Send drop message to all child objects
                base.SendMessage("OnPartDropped", SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                KAS_Shared.DebugWarning("Drop - Part not grabbed, ignoring drop...");
            }
        }

        public void RefreshContextMenu()
        {
            if (grabbed)
            {
                Events["ContextMenuGrab"].guiActiveUnfocused = false;
                Events["ContextMenuDrop"].guiActiveUnfocused = true;
                Events["ContextMenuDrop"].guiActive = true;
            }
            else
            {
                Events["ContextMenuGrab"].guiActiveUnfocused = true;
                Events["ContextMenuDrop"].guiActiveUnfocused = false;
                Events["ContextMenuDrop"].guiActive = false;
            }

            Events["ContextMenuGrab"].unfocusedRange = KASAddonControlKey.radius;
            Events["ContextMenuGrab"].guiName = "Grab" + " (Key " + KASAddonControlKey.grabPartKey.ToUpper() + ")";

            if (attachOnPart || attachOnEva || attachOnStatic)
            {
                Events["ContextMenuEvaAttach"].guiActiveUnfocused = true;
                if (grabbed) Events["ContextMenuEvaAttach"].guiActive = true;
                else Events["ContextMenuEvaAttach"].guiActive = false;
            }
            else
            {
                Events["ContextMenuEvaAttach"].guiActiveUnfocused = false;
                Events["ContextMenuEvaAttach"].guiActive = false;
            }

            KAS_Shared.InvalidateContextMenu(this.part);
        }

        [KSPEvent(name = "ContextMenuGrab", active = true, guiActiveUnfocused = false, guiActive = false, unfocusedRange = 2f, guiName = "Grab")]
        public void ContextMenuGrab()
        {
            if (!part.packed)
            {
                Grab(FlightGlobals.ActiveVessel);
            }
        }

        [KSPEvent(name = "ContextMenuDrop", active = true, guiActiveUnfocused = false, guiActive = false, unfocusedRange = 2f, guiName = "Drop")]
        public void ContextMenuDrop()
        {
            if (!part.packed)
            {
                Drop();
            }
        }

        [KSPEvent(name = "ContextMenuEvaAttach", active = true, guiActiveUnfocused = false, guiActive = false, guiName = "Attach")]
        public void ContextMenuEvaAttach()
        {
            if (attachOnPart || attachOnEva || attachOnStatic)
            {
                if (KASAddonPointer.isRunning)
                {
                    KASAddonPointer.StopPointer();
                }
                else
                {
                    KASAddonPointer.StartPointer(this.part, KASAddonPointer.PointerMode.MoveAndAttach, attachOnPart, attachOnEva, attachOnStatic, attachMaxDist, this.part.transform, attachSendMsgOnly);
                }
            }
            else
            {
                ScreenMessages.PostScreenMessage("This part cannot be attached", 5, ScreenMessageStyle.UPPER_CENTER);
            }
        }
    }
}
