using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Data
{
    // 시뮬 전역 파라미터(런타임 값)
    public struct SimParams
    {
        public float2 worldSize;
        public bool wrapEdges;
        public float dt;
        public float radius;
        public float mass;
        public float sameAttract;
        public float diffRepel;
        public float stiffRepel;
        public float viscosity;
        public float noise;
        public float maxSpeed;

        public byte playerEnabled;
        public float2 playerPos;
        public float2 playerVel;
        public float playerRadius;
        public float playerForce;
        public float playerRange;

        public float playerEatRadius;
        public float wbcChaseForce;
        public float wbcLatchRadius;
        public float wbcLatchSpring;
        public float shakeBreakSpeed;

        public byte wbcEnabled;

        public uint tick;
    }
  }
