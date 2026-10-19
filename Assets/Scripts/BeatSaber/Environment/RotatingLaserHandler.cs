// ==============================================================================
//  RotatingLaserHandler - Rotates laser targets (port, GUID-faithful).
// ==============================================================================
//  The filename/GUID stay identical to ArcViewer (RotatingLaserHandler.cs.meta) so
//  that the ported environment prefabs keep their script reference.
//  `targets`/`eventType`/`rotationAxis` are exactly the serialized names.
// ==============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace QuestSaber.Environment
{
    public class RotatingLaserHandler : MonoBehaviour
    {
        [SerializeField] protected List<Transform> targets;
        [SerializeField] protected LightEventType eventType;
        [SerializeField] protected RotationAxis rotationAxis;

        protected List<Vector3> defaultRotations = new List<Vector3>();

        public virtual void UpdateLaserRotations(LaserSpeedEvent laserSpeedEvent, LightEventType type)
        {
            if (type != eventType) return;

            if (laserSpeedEvent == null)
            {
                ResetRotations();
                return;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                float angle = laserSpeedEvent.GetLaserRotation(EnvironmentClock.CurrentTime, i);
                SetLaserRotation(targets[i], angle, defaultRotations[i]);
            }
        }

        protected void ResetRotations()
        {
            for (int i = 0; i < targets.Count; i++)
                SetLaserRotation(targets[i], 0f, defaultRotations[i]);
        }

        protected void SetLaserRotation(Transform target, float angle, Vector3 defaultRotation)
        {
            Vector3 rotation = defaultRotation;
            switch (rotationAxis)
            {
                case RotationAxis.X: rotation.x = angle; break;
                case RotationAxis.Y: rotation.y = angle; break;
                case RotationAxis.Z: rotation.z = angle; break;
            }
            target.localEulerAngles = rotation;
        }

        protected virtual void Start()
        {
            EnvironmentLightManager.OnLaserRotationsChanged += UpdateLaserRotations;

            defaultRotations.Clear();
            if (targets == null) return;
            foreach (Transform target in targets)
            {
                if (target != null) defaultRotations.Add(target.localEulerAngles);
            }
        }

        protected virtual void OnDestroy()
        {
            EnvironmentLightManager.OnLaserRotationsChanged -= UpdateLaserRotations;
        }

        public enum RotationAxis { X, Y, Z }
    }
}
