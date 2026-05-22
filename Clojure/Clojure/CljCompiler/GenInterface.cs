/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

using clojure.lang.CljCompiler.Context;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace clojure.lang
{
    public static class GenInterface
    {
        #region Factory

        public static Type GenerateInterface(string iName, IPersistentMap attributes, Seqable extends, ISeq methods)
        {
            iName = iName.Replace('-', '_');

            GenContext context = CurrentAotGenerationContext();
            if (context is null)
#if NETFRAMEWORK
                context = GenContext.CreateWithExternalAssembly(iName+"_"+RT.nextID(), ".dll", false);
#else
                context = GenContext.CreateWithInternalAssembly(iName + "_" + RT.nextID(), false);
#endif

            for (ISeq s = RT.seq(extends); s != null; s = s.next())
            {
                object f = s.first();
                string name = f as String ?? ((Named)f).getName();

                if (name.Contains("-"))
                    throw new ArgumentException("Interface methods must not contain '-'");
            }


            Type[] interfaceTypes = GenClass.CreateTypeArray(extends?.seq());

            TypeBuilder proxyTB = context.ModuleBuilder.DefineType(
                iName,
                TypeAttributes.Interface | TypeAttributes.Public | TypeAttributes.Abstract,
                null,
                interfaceTypes);
            GeneratedTypeRecord generatedType = context.RegisterGeneratedType(
                "gen-interface:" + iName,
                iName,
                proxyTB);

            // Should we associate source file info?
            // See Java committ 8d6fdb, 2015.07.17, related to CLJ-1645
            // TODO: part of check on debug info

            SetCustomAttributes(context, proxyTB, attributes);

            DefineMethods(context, generatedType, proxyTB, methods);

            Type t = proxyTB.CreateType();
            context.RegisterGeneratedTypeCreated(generatedType, t);

            //if ( Compiler.IsCompiling )
            //    context.SaveAssembly();

            Compiler.RegisterDuplicateType(t);

            return t;
        }

        private static GenContext CurrentAotGenerationContext()
        {
            if (Compiler.CompilerContextVar.deref() is not GenContext context)
                return null;

            if (Compiler.IsCompiling)
                return context;

            GenerationContextPair generationContexts = Compiler.CurrentGenerationContext();
            return generationContexts is not null && generationContexts.HasPersistedContext
                ? context
                : null;
        }

        #endregion

        #region Fun with attributes

        // attributes = ( [ type inits]... }
        // inits = #{ init1 init2 ... }
        // init =  { :key value ... }
        // Special key :__args indicates positional arguments

        public static readonly Var ExtractAttributesVar = Var.intern(Namespace.findOrCreate(Symbol.intern("clojure.core")),Symbol.intern("extract-attributes"));

        public static IPersistentMap ExtractAttributes(IPersistentMap meta)
        {
            if (meta != null && ExtractAttributesVar.isBound)
                return (IPersistentMap)ExtractAttributesVar.invoke(meta);

            return PersistentArrayMap.EMPTY;
        }


        public static void SetCustomAttributes(TypeBuilder tb, IPersistentMap attributes)
        {
            SetCustomAttributes(CurrentCustomAttributeContext(), tb, attributes);
        }

        public static void SetCustomAttributes(GenContext context, TypeBuilder tb, IPersistentMap attributes)
        {
            foreach (CustomAttributeSpec spec in CreateCustomAttributeSpecs(attributes))
            {
                if (context is null)
                    tb.SetCustomAttribute(spec.CreateBuilder());
                else
                    context.SetCustomAttribute(
                        tb,
                        spec.Constructor,
                        spec.ConstructorArgs,
                        spec.Properties,
                        spec.PropertyValues,
                        spec.Fields,
                        spec.FieldValues);
            }
        }

        public static void SetCustomAttributes(FieldBuilder fb, IPersistentMap attributes)
        {
            SetCustomAttributes(CurrentCustomAttributeContext(), fb, attributes);
        }

        public static void SetCustomAttributes(GenContext context, FieldBuilder fb, IPersistentMap attributes)
        {
            foreach (CustomAttributeSpec spec in CreateCustomAttributeSpecs(attributes))
            {
                if (context is null)
                    fb.SetCustomAttribute(spec.CreateBuilder());
                else
                    context.SetCustomAttribute(
                        fb,
                        spec.Constructor,
                        spec.ConstructorArgs,
                        spec.Properties,
                        spec.PropertyValues,
                        spec.Fields,
                        spec.FieldValues);
            }
        }

        public static void SetCustomAttributes(MethodBuilder mb, IPersistentMap attributes)
        {
            SetCustomAttributes(CurrentCustomAttributeContext(), mb, attributes);
        }

        public static void SetCustomAttributes(GenContext context, MethodBuilder mb, IPersistentMap attributes)
        {
            foreach (CustomAttributeSpec spec in CreateCustomAttributeSpecs(attributes))
            {
                if (context is null)
                    mb.SetCustomAttribute(spec.CreateBuilder());
                else
                    context.SetCustomAttribute(
                        mb,
                        spec.Constructor,
                        spec.ConstructorArgs,
                        spec.Properties,
                        spec.PropertyValues,
                        spec.Fields,
                        spec.FieldValues);
            }
        }

        public static void SetCustomAttributes(ParameterBuilder pb, IPersistentMap attributes)
        {
            SetCustomAttributes(CurrentCustomAttributeContext(), pb, attributes);
        }

        public static void SetCustomAttributes(GenContext context, ParameterBuilder pb, IPersistentMap attributes)
        {
            foreach (CustomAttributeSpec spec in CreateCustomAttributeSpecs(attributes))
            {
                if (context is null)
                    pb.SetCustomAttribute(spec.CreateBuilder());
                else
                    context.SetCustomAttribute(
                        pb,
                        spec.Constructor,
                        spec.ConstructorArgs,
                        spec.Properties,
                        spec.PropertyValues,
                        spec.Fields,
                        spec.FieldValues);
            }
        }

        public static void SetCustomAttributes(ConstructorBuilder cb, IPersistentMap attributes)
        {
            SetCustomAttributes(CurrentCustomAttributeContext(), cb, attributes);
        }

        public static void SetCustomAttributes(GenContext context, ConstructorBuilder cb, IPersistentMap attributes)
        {
            foreach (CustomAttributeSpec spec in CreateCustomAttributeSpecs(attributes))
            {
                if (context is null)
                    cb.SetCustomAttribute(spec.CreateBuilder());
                else
                    context.SetCustomAttribute(
                        cb,
                        spec.Constructor,
                        spec.ConstructorArgs,
                        spec.Properties,
                        spec.PropertyValues,
                        spec.Fields,
                        spec.FieldValues);
            }
        }

        static readonly Keyword ARGS_KEY = Keyword.intern(null,"__args");

        private static GenContext CurrentCustomAttributeContext()
        {
            return Compiler.CompilerContextVar.deref() as GenContext;
        }

        private static List<CustomAttributeSpec> CreateCustomAttributeSpecs(IPersistentMap attributes)
        {
            List<CustomAttributeSpec> builders = new List<CustomAttributeSpec>();
            for (ISeq s = RT.seq(attributes); s != null; s = s.next())
                builders.AddRange(CreateCustomAttributeSpecs((IMapEntry)s.first()));
            return builders;
        }


        private static List<CustomAttributeSpec> CreateCustomAttributeSpecs(IMapEntry me)
        {
 
            Type t = (Type)me.key();
            IPersistentSet inits = (IPersistentSet)me.val();

            List<CustomAttributeSpec> builders = new List<CustomAttributeSpec>(inits.count());

            for (ISeq s = RT.seq(inits); s != null; s = s.next())
            {
                IPersistentMap init = (IPersistentMap)s.first();
                builders.Add(CreateCustomAttributeSpec(t, init));
            }

            return builders;
        }

        private static CustomAttributeSpec CreateCustomAttributeSpec(Type t, IPersistentMap args)
        {
            object[] ctorArgs = new object[0];
            Type[] ctorTypes = Type.EmptyTypes;

            List<PropertyInfo> pInfos = new List<PropertyInfo>();
            List<Object> pVals = new List<object>();
            List<FieldInfo> fInfos = new List<FieldInfo>();
            List<Object> fVals = new List<object>();

            for (ISeq s = RT.seq(args); s != null; s = s.next())
            {
                IMapEntry m2 = (IMapEntry)s.first();
                Keyword k = (Keyword) m2.key();
                object v = m2.val();
                if (k == ARGS_KEY)
                {
                    ctorArgs = GetCtorArgs((IPersistentVector)v);
                    ctorTypes = GetCtorTypes(ctorArgs);
                }
                else
                {
                    string name = k.Name;
                    PropertyInfo pInfo = t.GetProperty(name);
                    if (pInfo != null)
                    {
                        pInfos.Add(pInfo);
                        pVals.Add(v);
                        continue;
                    }

                    FieldInfo fInfo = t.GetField(name);
                    if (fInfo != null)
                    {
                        fInfos.Add(fInfo);
                        fVals.Add(v);
                        continue;
                    }
                    throw new ArgumentException(String.Format("Unknown field/property: {0} for attribute: {1}", k.Name, t.FullName));
                }
            }

            ConstructorInfo ctor = t.GetConstructor(ctorTypes);
            if (ctor == null)
                throw new ArgumentException(String.Format("Unable to find constructor for attribute: {0}", t.FullName));

            return new CustomAttributeSpec(
                ctor,
                ctorArgs,
                pInfos.ToArray(),
                pVals.ToArray(),
                fInfos.ToArray(),
                fVals.ToArray());
        }

        private static Type[] GetCtorTypes(object[] args)
        {
            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is null)
                    throw new ArgumentException("Custom attribute constructor arguments cannot be null.");

                types[i] = args[i] is Type ? typeof(Type) : args[i].GetType();
            }

            return types;
        }

        private static object[] GetCtorArgs(IPersistentVector v)
        {
            object[] args = new object[v.length()];
            for (int i = 0; i < v.length(); i++)
                args[i] = v.nth(i);

            return args;
        }

        private sealed class CustomAttributeSpec
        {
            internal CustomAttributeSpec(
                ConstructorInfo constructor,
                object[] constructorArgs,
                PropertyInfo[] properties,
                object[] propertyValues,
                FieldInfo[] fields,
                object[] fieldValues)
            {
                Constructor = constructor;
                ConstructorArgs = constructorArgs;
                Properties = properties;
                PropertyValues = propertyValues;
                Fields = fields;
                FieldValues = fieldValues;
            }

            internal ConstructorInfo Constructor { get; }
            internal object[] ConstructorArgs { get; }
            internal PropertyInfo[] Properties { get; }
            internal object[] PropertyValues { get; }
            internal FieldInfo[] Fields { get; }
            internal object[] FieldValues { get; }

            internal CustomAttributeBuilder CreateBuilder()
            {
                return new CustomAttributeBuilder(
                    Constructor,
                    ConstructorArgs,
                    Properties,
                    PropertyValues,
                    Fields,
                    FieldValues);
            }
        }
        
        
        #endregion

        #region Defining methods

        private static void DefineMethods(
            GenContext context,
            GeneratedTypeRecord generatedType,
            TypeBuilder proxyTB,
            ISeq methods)
        {
            for (ISeq s = methods?.seq(); s != null; s = s.next())
                DefineMethod(context, generatedType, proxyTB, (IPersistentVector)s.first());
        }

        private static void DefineMethod(
            GenContext context,
            GeneratedTypeRecord generatedType,
            TypeBuilder proxyTB,
            IPersistentVector sig)
        {
            Symbol mname = (Symbol)sig.nth(0);
            Type[] paramTypes = GenClass.CreateTypeArray((ISeq)sig.nth(1));
            Type retType = (Type)sig.nth(2);
            ISeq pmetas = (ISeq)(sig.count() >= 4 ? sig.nth(3) : null);

            MethodBuilder mb = context.DefineMethod(
                proxyTB,
                mname.Name,
                MethodAttributes.Abstract | MethodAttributes.Public | MethodAttributes.Virtual,
                retType,
                paramTypes);
            context.RegisterGeneratedMember(generatedType, GeneratedMemberKind.Method, mname.Name, mb);

            SetCustomAttributes(context, mb, GenInterface.ExtractAttributes(RT.meta(mname)));
            int i=1;
            for (ISeq s = pmetas; s != null; s = s.next(), i++)
            {
                IPersistentMap meta = GenInterface.ExtractAttributes((IPersistentMap)s.first());
                if (meta != null && meta.count() > 0)
                {
                    ParameterBuilder pb = mb.DefineParameter(i, ParameterAttributes.None, String.Format("p_{0}",i));
                    GenInterface.SetCustomAttributes(context, pb, meta);
                }
            }

        
        }

        #endregion
    }
}
