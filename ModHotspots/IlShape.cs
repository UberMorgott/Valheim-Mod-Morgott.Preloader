using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// Identifies a helper that mods embed by the exact IL of its methods, so a replacement is only applied to code
    /// it was proven equal to. Fingerprint = SHA-256 over every instruction (opcode + operand) of the method and of
    /// the compiler-generated methods it references (lambdas via ldftn, iterator/closure classes), with the helper's
    /// own namespace stripped: the same helper compiled into different mods yields the same fingerprint.
    /// </summary>
    internal static class IlShape
    {
        public static string Fingerprint(MethodInfo method)
        {
            var sb = new StringBuilder();
            Append(sb, method, method.DeclaringType, new HashSet<MethodBase>());
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return string.Concat(hash.Take(12).Select(b => b.ToString("x2")));
            }
        }

        private static void Append(StringBuilder sb, MethodBase method, Type helper, HashSet<MethodBase> seen)
        {
            if (!seen.Add(method))
            {
                return;
            }
            sb.Append("method ").Append(Member(method, helper)).Append('\n');
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method);
            var labelIndex = new Dictionary<Label, int>();
            for (int i = 0; i < code.Count; i++)
            {
                foreach (Label label in code[i].labels)
                {
                    labelIndex[label] = i;
                }
            }
            var nested = new List<MethodBase>();
            foreach (CodeInstruction ci in code)
            {
                sb.Append(ci.opcode.Name).Append(' ').Append(Operand(ci.operand, helper, labelIndex));
                foreach (ExceptionBlock block in ci.blocks)
                {
                    sb.Append(" [").Append(block.blockType).Append(' ').Append(block.catchType?.FullName).Append(']');
                }
                sb.Append('\n');
                if (ci.operand is MethodBase callee && IsCompilerGenerated(callee.DeclaringType, helper))
                {
                    nested.Add(callee);
                }
            }
            foreach (MethodBase callee in nested)
            {
                Append(sb, callee, helper, seen);
            }
        }

        /// <summary>Lambda/closure/iterator classes nested in the helper type (e.g. <c>ShaderReplacer+&lt;&gt;c</c>).</summary>
        private static bool IsCompilerGenerated(Type type, Type helper)
        {
            for (Type t = type; t != null; t = t.DeclaringType)
            {
                if (t.DeclaringType == helper && t.Name.StartsWith("<", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Operand(object operand, Type helper, Dictionary<Label, int> labelIndex)
        {
            switch (operand)
            {
                case null:
                    return "";
                case Label label:
                    return "L" + (labelIndex.TryGetValue(label, out int i) ? i : -1);
                case Label[] labels:
                    return string.Join(",", labels.Select(l => "L" + (labelIndex.TryGetValue(l, out int j) ? j : -1)).ToArray());
                case MemberInfo member:
                    return Member(member, helper);
                case LocalBuilder local:
                    return "loc" + local.LocalIndex + ":" + TypeName(local.LocalType, helper);
                default:
                    return Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static string Member(MemberInfo member, Type helper)
        {
            if (member is Type type)
            {
                return TypeName(type, helper);
            }
            string owner = TypeName(member.DeclaringType, helper);
            if (member is MethodBase method)
            {
                string generic = method.IsGenericMethod
                    ? "<" + string.Join(",", method.GetGenericArguments().Select(t => TypeName(t, helper)).ToArray()) + ">"
                    : "";
                string parameters = string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType, helper)).ToArray());
                return owner + "::" + method.Name + generic + "(" + parameters + ")";
            }
            return owner + "::" + member.Name;
        }

        /// <summary>
        /// Assembly-independent type name: generic arguments spelled out recursively (no assembly-qualified names),
        /// the helper's namespace removed, and the mod's anonymous types (numbered per whole assembly by the compiler)
        /// reduced to their generic arity.
        /// </summary>
        private static string TypeName(Type type, Type helper)
        {
            if (type == null)
            {
                return "";
            }
            if (type.IsGenericParameter)
            {
                return "!" + type.Name;
            }
            if (type.HasElementType)
            {
                string suffix = type.IsArray ? "[" + new string(',', type.GetArrayRank() - 1) + "]" : type.IsByRef ? "&" : "*";
                return TypeName(type.GetElementType(), helper) + suffix;
            }
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                return TypeName(type.GetGenericTypeDefinition(), helper) + "<" +
                       string.Join(",", type.GetGenericArguments().Select(t => TypeName(t, helper)).ToArray()) + ">";
            }
            string name = type.FullName ?? type.Name;
            if (type.Assembly != helper.Assembly)
            {
                return name;
            }
            if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
            {
                return "~anon`" + type.GetGenericArguments().Length;
            }
            string ns = helper.Namespace;
            if (!string.IsNullOrEmpty(ns) && name.StartsWith(ns + ".", StringComparison.Ordinal))
            {
                name = name.Substring(ns.Length + 1);
            }
            return "~" + name;
        }
    }
}
