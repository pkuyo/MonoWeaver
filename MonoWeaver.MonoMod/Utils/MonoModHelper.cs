using System;
using System.Collections.Generic;
using System.Text;
using MonoMod.Cil;
using MonoWeaver.MonoMod.Detour;

namespace MonoWeaver.MonoMod.Utils
{
    public static class MonoModHelper
    {
        public static ILContext.Manipulator ToMaipulator(this ILCfgContext.CFGManipulator manipulator)
        {
            return il =>
            {
                var cfgIl = new ILCfgContext(il);
                manipulator.Invoke(cfgIl);
                cfgIl.Analyzer.ReAnalyze();
            };
        }
    }
}
