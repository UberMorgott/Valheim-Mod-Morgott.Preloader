using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Methods the probes must never patch. HarmonyX 2.9.0 (BepInEx\core\0Harmony.dll,
    /// <c>HarmonyManipulator.MakeReturnLabel</c>) turns every <c>ret</c> of the original into a branch to one <c>ret</c> it
    /// appends after the original's last instruction. When that last instruction closes an exception handler that runs to
    /// the method end (an endless coroutine whose loop ends with a <c>foreach</c>: the compiler puts the <c>finally</c>
    /// last), the appended <c>ret</c> lands inside the handler and every former <c>ret</c> branches into it: the patched copy
    /// is invalid IL, whatever the patch kind. Unity's Mono dies natively compiling it (RuntimeMethodHandle.GetFunctionPointer
    /// in MonoMod's GetNativeStart; reproduced with Unity 6000.0.75 mono.exe: "Invalid instruction target").
    /// </summary>
    internal static class DetourSafety
    {
        internal static bool EndsInHandler(MethodBody body)
        {
            int ilLength = body.GetILAsByteArray().Length;
            IList<ExceptionHandlingClause> clauses = body.ExceptionHandlingClauses;
            for (int i = 0; i < clauses.Count; i++)
            {
                ExceptionHandlingClause c = clauses[i];
                if (c.HandlerOffset + c.HandlerLength >= ilLength || c.TryOffset + c.TryLength >= ilLength)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when a protected region (handler, or a try block placed after its handler) reaches the end of the
        /// method's IL (<paramref name="ilLength"/> bytes): the appended ret would land inside it.</summary>
        internal static bool EndsInHandler(int ilLength, IReadOnlyList<(int Offset, int Length)> handlers)
        {
            for (int i = 0; i < handlers.Count; i++)
            {
                (int offset, int length) = handlers[i];
                if (offset + length >= ilLength)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
