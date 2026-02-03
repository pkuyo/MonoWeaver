// See https://aka.ms/new-console-template for more information
using Mono.Cecil;
using MonoWeaver.Utils;
using System.Collections;

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(@"C:\Work\my-source\MonoWeaver\MonoWeaver.Demo\bin\Debug\net10.0");
AssemblyDefinition ass = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("test",new Version(1,0)), "test", new ModuleParameters()
{
    AssemblyResolver = resolver,
});
var m = ass.MainModule;

var type1 = m.ImportReference(typeof(IEnumerable<>));
var def1 = type1.Resolve();
var type2 = m.ImportReference(typeof(B<>));
var def2 = type2.Resolve();
var r = type1.IsAssignableFrom(type2);
Console.WriteLine(r);
r = type2.IsAssignableFrom(type1);
Console.WriteLine(r);


class A<T> : IEnumerable<T>
{
    public IEnumerator<T> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

class B<T> : A<T> where T : class
{

}