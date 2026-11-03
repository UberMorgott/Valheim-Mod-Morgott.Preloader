using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// Identifies a helper that mods embed by the exact IL of its methods, so a replacement only applies to code it was
    /// proven equal to. Fingerprint = SHA-256 over every instruction (opcode + operand) of the method and of the
    /// compiler-generated methods it references (lambdas, iterator/closure classes), with the helper's own namespace
    /// stripped: the same helper compiled into different mods yields the same fingerprint.
    /// </summary>
    internal static class IlShape
    {
        public static string Fingerprint(MethodInfo method)
        {
            var sb = new StringBuilder();
            Append(sb, method, method.DeclaringType, new HashSet<MethodBase>());
            return Hash.Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString())).Substring(0, 24);
        }

        private static void Append(StringBuilder sb, MethodBase method, Type helper, HashSet<MethodBase> seen)
        {
            if (!seen.Add(method)) return;
            sb.Append("method ").Append(Member(method, helper)).Append('\n');
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method);
            var labelIndex = new Dictionary<Label, int>();
            for (int i = 0; i < code.Count; i++)
            {
                foreach (Label label in code[i].labels) labelIndex[label] = i;
            }
            string L(Label l) => "L" + (labelIndex.TryGetValue(l, out int i) ? i : -1);
            var nested = new List<MethodBase>();
            foreach (CodeInstruction ci in code)
            {
                string operand;
                switch (ci.operand)
                {
                    case null: operand = ""; break;
                    case Label label: operand = L(label); break;
                    case Label[] labels: operand = string.Join(",", labels.Select(L).ToArray()); break;
                    case MemberInfo member: operand = Member(member, helper); break;
                    case LocalBuilder local: operand = "loc" + local.LocalIndex + ":" + TypeName(local.LocalType, helper); break;
                    default: operand = Convert.ToString(ci.operand, System.Globalization.CultureInfo.InvariantCulture); break;
                }
                sb.Append(ci.opcode.Name).Append(' ').Append(operand);
                foreach (ExceptionBlock block in ci.blocks) sb.Append(" [").Append(block.blockType).Append(' ').Append(block.catchType?.FullName).Append(']');
                sb.Append('\n');
                if (ci.operand is MethodBase callee && IsCompilerGenerated(callee.DeclaringType, helper)) nested.Add(callee);
            }
            foreach (MethodBase callee in nested) Append(sb, callee, helper, seen);
        }

        /// <summary>Lambda/closure/iterator classes nested in the helper type (e.g. <c>ShaderReplacer+&lt;&gt;c</c>).</summary>
        private static bool IsCompilerGenerated(Type type, Type helper)
        {
            for (Type t = type; t != null; t = t.DeclaringType)
            {
                if (t.DeclaringType == helper && t.Name.StartsWith("<", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string Member(MemberInfo member, Type helper)
        {
            if (member is Type type) return TypeName(type, helper);
            string owner = TypeName(member.DeclaringType, helper);
            if (!(member is MethodBase method)) return owner + "::" + member.Name;
            string generic = method.IsGenericMethod ? "<" + Names(method.GetGenericArguments(), helper) + ">" : "";
            return owner + "::" + method.Name + generic + "(" + Names(method.GetParameters().Select(p => p.ParameterType), helper) + ")";
        }

        private static string Names(IEnumerable<Type> types, Type helper) => string.Join(",", types.Select(t => TypeName(t, helper)).ToArray());

        /// <summary>Assembly-independent type name: generic arguments spelled out (no assembly-qualified names), the
        /// helper's namespace removed, the mod's anonymous types (numbered per assembly) reduced to their arity.</summary>
        private static string TypeName(Type type, Type helper)
        {
            if (type == null) return "";
            if (type.IsGenericParameter) return "!" + type.Name;
            if (type.HasElementType)
                return TypeName(type.GetElementType(), helper) + (type.IsArray ? "[" + new string(',', type.GetArrayRank() - 1) + "]" : type.IsByRef ? "&" : "*");
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
                return TypeName(type.GetGenericTypeDefinition(), helper) + "<" + Names(type.GetGenericArguments(), helper) + ">";
            string name = type.FullName ?? type.Name;
            if (type.Assembly != helper.Assembly) return name;
            if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)) return "~anon`" + type.GetGenericArguments().Length;
            string ns = helper.Namespace;
            return "~" + (!string.IsNullOrEmpty(ns) && name.StartsWith(ns + ".", StringComparison.Ordinal) ? name.Substring(ns.Length + 1) : name);
        }
    }
}
