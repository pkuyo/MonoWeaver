using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace MonoWeaver.MonoMod.Detour
{
    public class ILCfgContext
    {
        public delegate void CFGManipulator(ILCfgContext context);
        public ILCfgContext(ILContext inner)
        {
            InnerContext = inner;
            Analyzer = inner.Body.Analyze().ThrowIfHasErrors();
            Graph = Analyzer.ToGraph();
        }

        public ILCFGraph Graph { get; }

        public ILMethodAnalyzer Analyzer { get; }

        internal ILContext InnerContext { get; }
        public void MakeReadOnly()
        {
            InnerContext.MakeReadOnly();
        }

        public FieldReference Import(FieldInfo field)
        {
            return InnerContext.Import(field);
        }

        public MethodReference Import(MethodBase method)
        {
            return InnerContext.Import(method);
        }

        public TypeReference Import(Type type)
        {
            return InnerContext.Import(type);
        }

        public ILLabel DefineLabel()
        {
            return InnerContext.DefineLabel();
        }

        public ILLabel DefineLabel(Instruction target)
        {
            return InnerContext.DefineLabel(target);
        }

        public int IndexOf(Instruction instr)
        {
            return InnerContext.IndexOf(instr);
        }

        public IEnumerable<ILLabel> GetIncomingLabels(Instruction instr)
        {
            return InnerContext.GetIncomingLabels(instr);
        }

        public int AddReference<T>(T t)
        {
            return InnerContext.AddReference(t);
        }

        public MethodDefinition Method => InnerContext.Method;

        public MethodBody Body => InnerContext.Body;

        public ModuleDefinition Module => InnerContext.Module;

        public ReadOnlyCollection<ILLabel> Labels => InnerContext.Labels;

        public bool IsReadOnly => InnerContext.IsReadOnly;
    }
}
