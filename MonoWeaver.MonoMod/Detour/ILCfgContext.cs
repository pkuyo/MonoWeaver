using MonoMod.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MonoWeaver.Detour
{
    public class ILCfgContext
    {
        public ILCfgContext(ILContext il)
        {
            ILContext = il;
            Analyzer = il.Body.Analyze().ThrowIfHasErrors();
            Graph = Analyzer.ToGraph();
        }

        public ILCFGraph Graph { get; }

        public ILMethodAnalyzer Analyzer { get; }

        public ILContext ILContext { get; }
    }
}
