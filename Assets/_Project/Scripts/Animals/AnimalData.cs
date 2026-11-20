using UnityEngine;

namespace AfkIsland.Animals
{
    [CreateAssetMenu(fileName = "AnimalData", menuName = "AFK Island/Animal")]
    public sealed class AnimalData : ScriptableObject
    {
        public string animalId = "butterfly";
        public string displayNameRu = "Бабочка";
        public GameObject prefab;
        public Color fallbackColor = new(0.95f, 0.55f, 0.2f);
        public float moveSpeed = 0.55f;
        public float idleSecondsMin = 1.5f;
        public float idleSecondsMax = 4f;
        public float wanderRadius = 1.8f;
        public Vector3 visualScale = new(0.25f, 0.25f, 0.25f);
        [Tooltip("Yaw correction from the animal root's +Z direction to the model's authored forward direction.")]
        public float modelForwardYawOffset = 0f;
    }
}
