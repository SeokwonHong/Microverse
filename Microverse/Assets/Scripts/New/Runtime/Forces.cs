using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Modules
{
    internal static class Forces
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 Repulsion(float2 n, float pen, float stiff) =>
            pen > 0f ? -n * (pen * stiff) : 0f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 Cohesion(float2 avgN, int count, float w, float cap)
        {
            if (count <= 0 || w <= 0f) return 0;
            float2 coh = (avgN / count) * w;
            float m = math.length(coh);
            return (m > cap) ? coh * (cap / (m + 1e-6f)) : coh;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 Separation(float2 avgN, int count, float w, float cap)
        {
            if (count <= 0 || w <= 0f) return 0;
            float2 sep = (avgN / count) * (-w);
            float m = math.length(sep);
            return (m > cap) ? sep * (cap / (m + 1e-6f)) : sep;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 Viscosity(float2 v, float mu) => -mu * v;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Hash(uint x)
        {
            x ^= 2747636419u; x *= 2654435769u;
            x ^= x >> 16; x *= 2654435769u;
            x ^= x >> 16; x *= 2654435769u;
            return x;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float U01(uint x) => (Hash(x) & 0x00FFFFFF) / (float)0x01000000;

        public static float2 RandCircle(uint a, uint b)
        {
            float ang = 6.28318530718f * U01(a);
            float r = U01(b) - 0.5f;
            return new float2(math.cos(ang), math.sin(ang)) * r;
        }
    }
}
