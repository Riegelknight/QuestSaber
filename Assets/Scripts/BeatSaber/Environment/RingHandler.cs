// ==============================================================================
//  RingHandler - rotates a ring light body (port, GUID-faithful).
// ==============================================================================
//  File name/GUID remain identical to ArcViewer (RingHandler.cs.meta), so that the
//  adopted ring prefabs keep their script reference. `bigRing`/`id` are
//  1:1 the serialized names and are set per ring instance in the prefab/scene.
// ==============================================================================
using UnityEngine;

namespace QuestSaber.Environment
{
    public class RingHandler : MonoBehaviour
    {
        [SerializeField] private bool bigRing;
        [SerializeField] private int id;

        public void UpdateRingRotations(RingRotationEventArgs eventArgs)
        {
            if ((!bigRing && !eventArgs.affectSmallRings) || (bigRing && !eventArgs.affectBigRings))
                return;

            RingRotationEvent current = null;
            for (int i = eventArgs.currentEventIndex; i >= 0; i--)
            {
                if (eventArgs.events[i].StartInfluenceTime(id) <= EnvironmentClock.CurrentTime)
                {
                    current = eventArgs.events[i];
                    break;
                }
            }

            if (current == null)
            {
                float defaultAngle = bigRing ? EnvironmentRingManager.BigRingStartAngle : EnvironmentRingManager.SmallRingStartAngle;
                float defaultStep = bigRing ? EnvironmentRingManager.BigRingStartStep : EnvironmentRingManager.SmallRingStartStep;
                SetRotation(defaultAngle + (defaultStep * id));
                return;
            }

            SetRotation(current.GetRingAngle(EnvironmentClock.CurrentTime, id));
        }

        private void SetRotation(float angle)
        {
            Vector3 eulerAngles = transform.localEulerAngles;
            eulerAngles.z = angle % 360;
            transform.localEulerAngles = eulerAngles;
        }

        private void OnEnable()
        {
            EnvironmentRingManager.OnRingRotationsChanged += UpdateRingRotations;
        }

        private void OnDisable()
        {
            EnvironmentRingManager.OnRingRotationsChanged -= UpdateRingRotations;
        }
    }
}
