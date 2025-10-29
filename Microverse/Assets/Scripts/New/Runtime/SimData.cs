// CellSimulation 에도 변수 선언이 있긴 한데, 그건 읽기 용이고, 여기서 그 값을 읽어서 다음 프레임에 적용




using Unity.Collections;  
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

        // 플레이어/게임플레이
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

        public uint tick;
    }

    // SoA 버퍼
    public struct Buffers
    {
        public NativeArray<float2> posCur, posNext;
        public NativeArray<float2> velCur, velNext;
        public NativeArray<byte> species;
        public NativeArray<byte> state;       // 0=Alive 1=Dead 2=Latched
        public NativeArray<float2> latchOffset; // 라치된 백혈구 오프셋
        public NativeArray<float> latchTTL;    // 라치 잔여
        public NativeArray<float2> posSnap;     // PBD

        public NativeArray<float> squash;   // 0..1
        public NativeArray<float2> squashN;
        public void Dispose()
        {
            if (posCur.IsCreated) posCur.Dispose();
            if (posNext.IsCreated) posNext.Dispose();
            if (velCur.IsCreated) velCur.Dispose();
            if (velNext.IsCreated) velNext.Dispose();
            if (species.IsCreated) species.Dispose();
            if (state.IsCreated) state.Dispose();
            if (latchOffset.IsCreated) latchOffset.Dispose();
            if (latchTTL.IsCreated) latchTTL.Dispose();
            if (posSnap.IsCreated) posSnap.Dispose();

            if (squash.IsCreated) squash.Dispose();
            if (squashN.IsCreated) squashN.Dispose();
        }
    }
}
