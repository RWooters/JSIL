﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JSIL.Internal;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Linker.Steps;

namespace JSIL.Compiler.Extensibility.DeadCodeAnalyzer {
    using ICSharpCode.Decompiler;
    using ICSharpCode.Decompiler.ILAst;

    public class DeadCodeInfoProvider {
        private class MethodUsageInfo
        {
            private bool _hasNonVirualUsage;

            private List<TypeDefinition> _virtualUseFromType;

            private MethodDefinition _method;

            public MethodUsageInfo(MethodDefinition method)
            {
                if (method.IsVirtual) {
                    _virtualUseFromType = new List<TypeDefinition>();

                    // We need always preserve interface method signature.
                    if (method.DeclaringType.IsInterface) {
                        _hasNonVirualUsage = true;
                    }
                }
                else {
                    _hasNonVirualUsage = true;
                }

                _method = method;
            }

            private static bool IsDerivedType(TypeDefinition baseType, TypeDefinition probableDerived)
            {
                if (baseType == null || baseType.IsInterface)
                {
                    return true;
                }

                TypeReference testType = probableDerived;
                while (testType != null)
                {
                    var testTypeDef = testType.Resolve();
                    if (testTypeDef == baseType)
                    {
                        return true;
                    }

                    testType = testTypeDef.BaseType;
                }

                return false;
            }

            public void RegisterNonVirtualUsage()
            {
                _hasNonVirualUsage = true;
            }

            public void AddVirtualUsageType(TypeReference typeReference)
            {
                if (typeReference == null) {
                    _virtualUseFromType.Clear();
                    _virtualUseFromType.Add(null);
                }
                else {
                    var typeDefToAdd = typeReference.Resolve();

                    if (_virtualUseFromType.All(typeDefInList => !IsDerivedType(typeDefInList, typeDefToAdd))) {
                        var typesToDelete =
                            _virtualUseFromType.Where(typeDefInList => IsDerivedType(typeDefToAdd, typeDefInList))
                                .ToList();
                        _virtualUseFromType.Add(typeDefToAdd);
                        foreach (var typeToDelete in typesToDelete) {
                            _virtualUseFromType.Remove(typeToDelete);
                        }
                    }
                }
            }

            public bool HasVirtualUsage
            {
                get
                {
                    return _virtualUseFromType != null && _virtualUseFromType.Count > 0;
                }
            }

            public bool PresentRealMethodUsage
            {
                get
                {
                    return _hasNonVirualUsage || 
                           (_virtualUseFromType.Count == 1 &&
                            _virtualUseFromType[0] == null || _virtualUseFromType[0] == _method.DeclaringType);
                }
            }

            public bool IsIncluded(TypeDefinition typeToTest)
            {
                return _virtualUseFromType.Any(typeDefinition => IsDerivedType(typeDefinition, typeToTest));
            }
        }

        private readonly HashSet<AssemblyDefinition> Assemblies;
        private readonly HashSet<FieldDefinition> Fields;
        private readonly Dictionary<MethodDefinition, MethodUsageInfo> Methods;
        private readonly HashSet<TypeDefinition> Types;

        private readonly TypeMapStep TypeMapStep = new TypeMapStep();
        private readonly List<Regex> WhiteListCache;
        private readonly Configuration Configuration; 

        public DeadCodeInfoProvider(Configuration configuration) {
            Types = new HashSet<TypeDefinition>();
            Methods = new Dictionary<MethodDefinition, MethodUsageInfo>();
            Fields = new HashSet<FieldDefinition>();
            Assemblies = new HashSet<AssemblyDefinition>();

            Configuration = configuration;
            if (configuration.WhiteList != null &&
                configuration.WhiteList.Count > 0) {
                WhiteListCache = new List<Regex>(configuration.WhiteList.Count);
                foreach (var pattern in configuration.WhiteList) {
                    var compiledRegex = new Regex(pattern, RegexOptions.ECMAScript | RegexOptions.Compiled);
                    WhiteListCache.Add(compiledRegex);
                }
            }
        }

        internal TypeInfoProvider TypeInfoProvider { get; set; }

        public bool IsUsed(MemberReference member) {
            var typeReference = member as TypeReference;
            if (typeReference != null)
            {
                var defenition = typeReference.Resolve();
                return Types.Contains(defenition);
            }

            var methodReference = member as MethodReference;
            if (methodReference != null) {
                var defenition = methodReference.Resolve();
                return Methods.ContainsKey(defenition);
            }

            var fieldReference = member as FieldReference;
            if (fieldReference != null) {
                var defenition = fieldReference.Resolve();
                return Fields.Contains(defenition);
            }

            var propertyReference = member as PropertyReference;
            if (propertyReference != null)
            {
                var defenition = propertyReference.Resolve();
                if (defenition != null)
                {
                    if (defenition.GetMethod != null && Methods.ContainsKey(defenition.GetMethod))
                    {
                        return true;
                    }

                    if (defenition.SetMethod != null && Methods.ContainsKey(defenition.SetMethod))
                    {
                        return true;
                    }

                    if (defenition.OtherMethods != null &&
                        defenition.OtherMethods.Any(method => Methods.ContainsKey(method)))
                    {
                        return true;
                    }
                }

                return false;
            }

            var eventReference = member as EventReference;
            if (eventReference != null)
            {
                var defenition = eventReference.Resolve();

                if (defenition != null)
                {
                    if (defenition.AddMethod != null && Methods.ContainsKey(defenition.AddMethod))
                    {
                        return true;
                    }

                    if (defenition.RemoveMethod != null && Methods.ContainsKey(defenition.RemoveMethod))
                    {
                        return true;
                    }

                    if (defenition.InvokeMethod != null && Methods.ContainsKey(defenition.InvokeMethod))
                    {
                        return true;
                    }

                    if (defenition.OtherMethods != null &&
                        defenition.OtherMethods.Any(method => Methods.ContainsKey(method)))
                    {
                        return true;
                    }
                }

                return false;
            }

            throw new ArgumentException("Unexpected member reference type");
        }

        public void WalkMethod(MethodReference methodReference, TypeReference targetType = null, bool virt = false)
        {
            if (!AddMethod(methodReference, targetType, virt)) {
                return;
            }

            var method = methodReference.Resolve();

            var context = new DecompilerContext(method.Module)
            {
                Settings =
                {
                    AnonymousMethods = true,
                    AsyncAwait = false,
                    YieldReturn = false,
                    QueryExpressions = false,
                    LockStatement = false,
                    FullyQualifyAmbiguousTypeNames = true,
                    ForEachStatement = false,
                    ExpressionTrees = false,
                    ObjectOrCollectionInitializers = false,
                },
                CurrentModule = method.Module,
                CurrentMethod = method,
                CurrentType = method.DeclaringType
            };

            List<Instruction> foundInstructions = (from instruction in method.Body.Instructions
                where method.HasBody && method.Body.Instructions != null && instruction.Operand != null
                select instruction).ToList();

            IEnumerable<TypeReference> typesFound = from instruction in foundInstructions
                let tRef = instruction.Operand as TypeReference
                where tRef != null
                select tRef;

            IEnumerable<FieldDefinition> fieldsFound = from instruction in foundInstructions
                let fRef = instruction.Operand as FieldReference
                where fRef != null && fRef.FieldType != null
                let fRefResolved = fRef.Resolve()
                where fRefResolved != null
                select fRefResolved;
            foreach (TypeReference typeDefinition in typesFound) {
                AddType(typeDefinition);
            }

            foreach (FieldDefinition fieldDefinition in fieldsFound) {
                AddField(fieldDefinition);
            }

            ILBlock ilb = null;
            bool useSimpleMethodWalk = Configuration.NonAggressiveVirtualMethodElimination;
            if (!useSimpleMethodWalk) {
                try {
                    var decompiler = new ILAstBuilder();
                    var optimizer = new ILAstOptimizer();

                    ilb = new ILBlock(decompiler.Build(method, false, context));
                    optimizer.Optimize(context, ilb);
                }
                catch (Exception) {
                    useSimpleMethodWalk = true;
                }
            }

            if (!useSimpleMethodWalk) {
                var expressions = ilb.GetSelfAndChildrenRecursive<ILExpression>();

                foreach (var ilExpression in expressions) {
                    var mRef = ilExpression.Operand as MethodReference;
                    if (mRef != null && mRef.DeclaringType != null) {
                        bool isVirtual = false;
                        TypeReference thisArg = null;

                        switch (ilExpression.Code)
                        {
                            case ILCode.Ldftn:
                            case ILCode.Newobj:
                                break;

                            case ILCode.Jmp:
                            case ILCode.Call:
                            case ILCode.CallGetter:
                            case ILCode.CallSetter:
                                thisArg = mRef.HasThis ? ilExpression.Arguments[0].InferredType : null;
                                break;
                            case ILCode.CallvirtGetter:
                            case ILCode.CallvirtSetter:
                            case ILCode.Callvirt:
                                isVirtual = true;
                                thisArg = ilExpression.Arguments[0].InferredType;
                                break;

                            case ILCode.Ldvirtftn:
                            case ILCode.Ldtoken:
                                isVirtual = true;
                                break;
                        }

                        WalkMethod(mRef, thisArg, isVirtual);
                    }
                }                
            }
            else {
                IEnumerable<MethodReference> methodsFound = from instruction in foundInstructions
                                                            let mRef = instruction.Operand as MethodReference
                                                            where mRef != null && mRef.DeclaringType != null
                                                            select mRef;

                foreach (MethodReference methodDefinition in methodsFound) {
                    if (methodDefinition != method) {
                        WalkMethod(methodDefinition, null, true);
                    }
                }
            }
        }

        public void ResolveVirtualMethodsCycle()
        {
            var inintialMemberCount = 0;
            var endMemberCount = 0;
            do
            {
                inintialMemberCount = Fields.Count + Methods.Count + Types.Count;
                ResolveVirtualMethods();
                endMemberCount = Fields.Count + Methods.Count + Types.Count;
            } while (endMemberCount != inintialMemberCount);

            if (!Configuration.NonAggressiveVirtualMethodElimination) {
                foreach (var type in Types.Where(item => !item.IsInterface)) {
                    var baseMap = new HashSet<MethodDefinition>();

                    var currentType = type;
                    do {
                        foreach (var method in currentType.Methods.Where(item => item.IsVirtual)) {
                            MethodUsageInfo methodUsageInfo;

                            if (Methods.TryGetValue(method, out methodUsageInfo)) {
                                if (!baseMap.Contains(method) && methodUsageInfo.IsIncluded(type)) {

                                    methodUsageInfo.RegisterNonVirtualUsage();
                                }
                            }

                            var localBase = TypeMapStep.Annotations.GetBaseMethods(method);
                            if (localBase != null) {
                                baseMap.UnionWith(localBase);
                            }
                        }

                        currentType = currentType.BaseType != null ? currentType.BaseType.Resolve() : null;
                    } while (currentType != null);
                }

                var methodsToRemove =
                    Methods
                        .Where(item => !item.Value.PresentRealMethodUsage)
                        .Select(item => item.Key)
                        .ToList();

                foreach (var methodDefinition in methodsToRemove) {
                    Methods.Remove(methodDefinition);
                }
            }
        }

        public void AddAssemblies(IEnumerable<AssemblyDefinition> assemblies)
        {
            IEnumerable<ModuleDefinition> modules = from assembly in assemblies
                                                    from module in assembly.Modules
                                                    select module;

            foreach (ModuleDefinition module in modules)
            {
                TypeMapStep.ProcessModule(module);

                foreach (var type in module.Types)
                {
                    ProcessWhiteList(type);
                }
            }

            Assemblies.UnionWith(assemblies);
        }

        private void ResolveVirtualMethods()
        {
            var tempMethods = Methods.Where(pair => pair.Value.HasVirtualUsage).ToList();

            foreach (var pair in tempMethods)
            {
                ResolveVirtualMethod(pair.Key, pair.Value);
            }
        }

        private void ResolveVirtualMethod(MethodDefinition method, MethodUsageInfo usageInfo) {
            var overrides = new HashSet<MethodDefinition>();
            GetAllOverrides(method, overrides);
            foreach (MethodDefinition methodDefinition in overrides) {
                if (IsUsed(methodDefinition.DeclaringType)) {
                    if (usageInfo.IsIncluded(methodDefinition.DeclaringType.Resolve()))
                    {
                        WalkMethod(methodDefinition);
                    }
                }
            }
        }

        private void AddType(TypeReference type) {
            if (type == null || IsIgnored(type))
            {
                return;
            }

            if (type.IsGenericInstance)
            {
                var genericType = (GenericInstanceType)type;
                foreach (var genericArgument in genericType.GenericArguments) {
                    AddType(genericArgument);
                }
            }

            TypeDefinition resolvedType = type.Resolve();
            if (resolvedType != null)
            {
                AddType(resolvedType);
            }
        }

        private void AddType(TypeDefinition resolvedType)
        {
            if (resolvedType == null)
            {
                return;
            }

            if (Types.Add(resolvedType))
            {
                AddType(resolvedType.BaseType);

                if (resolvedType.HasCustomAttributes)
                {
                    foreach (CustomAttribute attribute in resolvedType.CustomAttributes)
                    {
                        if (attribute.HasConstructorArguments) {
                            WalkMethod(attribute.Constructor.Resolve());
                        }
                    }
                }

                // HACK: force analyze static constructor
                MethodDefinition cctor = resolvedType.Methods.FirstOrDefault(m => m.Name == ".cctor");
                if (cctor != null && cctor.HasBody)
                {
                    WalkMethod(cctor);
                }
            }
        }

        private bool IsIgnored(TypeReference type)
        {
            if (type == null)
            {
                return false;
            }

            var typeDefenition = type.Resolve();
            if (typeDefenition != null)
            {
                var typeInformation = TypeInfoProvider.GetTypeInformation(type);
                if (typeInformation.IsIgnored)
                {
                    return true;
                }
            }

            if (type.IsGenericInstance)
            {
                var genericType = (GenericInstanceType) type;
                if (genericType.GenericArguments.Any(IsIgnored))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsIgnored(FieldReference field)
        {
            var fieldDefenition = field.Resolve();
            if (fieldDefenition != null)
            {
                var fieldInfo = TypeInfoProvider.GetMemberInformation<FieldInfo>(fieldDefenition);
                if (fieldInfo.IsIgnored)
                {
                    return true;
                }
            }

            if (field.DeclaringType.IsGenericInstance)
            {
                if (IsIgnored(field.DeclaringType))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsIgnored(MethodReference method)
        {
            var methodDefenition = method.Resolve();
            if (methodDefenition != null)
            {
                var methodInfo = TypeInfoProvider.GetMemberInformation<MethodInfo>(methodDefenition);
                if (methodInfo.IsIgnored)
                {
                    return true;
                }
            }

            if (method.IsGenericInstance)
            {
                var genericMethod = (GenericInstanceMethod) method;
                if (genericMethod.GenericArguments.Any(IsIgnored))
                {
                    return true;
                }
            }

            if (method.DeclaringType.IsGenericInstance)
            {
                if (IsIgnored(method.DeclaringType))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsStubOrExternal(MethodReference method)
        {
            var methodDefenition = method.Resolve();
            if (methodDefenition != null)
            {
                var methodInfo = TypeInfoProvider.GetMemberInformation<MethodInfo>(methodDefenition);
                if (methodInfo.IsExternal || methodInfo.DeclaringType.IsExternal || methodInfo.DeclaringType.IsStubOnly)
                {
                    return true;
                }
            }

            return false;
        }

        private bool AddMethod(MethodReference method, TypeReference targetType, bool isVirt) {
            if (method == null || method.DeclaringType.IsArray) {
                return false;
            }

            if (IsIgnored(method))
            {
                return false;
            }

            if (method.IsGenericInstance)
            {
                var genericMethod = (GenericInstanceMethod)method;
                foreach (var genericParameter in genericMethod.GenericArguments)
                {
                    AddType(genericParameter);
                }
            }

            AddType(method.DeclaringType);
            AddType(method.ReturnType);
            foreach (var parameterDefinition in method.Parameters)
            {
                AddType(parameterDefinition.ParameterType);
            }

            MethodDefinition resolvedMethod = method.Resolve();

            if (resolvedMethod == null)
            {
                return false;
            }

            if (AddMethodToList(resolvedMethod, targetType, isVirt) && resolvedMethod.HasBody)
            {
                //if (resolvedMethod.HasCustomAttributes) {
                //    foreach (CustomAttribute attribute in resolvedMethod.CustomAttributes) {
                //        AddType(attribute.AttributeType);
                //    }
                //}
                
                if (IsStubOrExternal(method))
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        private bool AddMethodToList(MethodDefinition method, TypeReference targetType, bool isVirt)
        {
            MethodUsageInfo virualCallsList;
            bool found = true;
            if (!Methods.TryGetValue(method, out virualCallsList)) {
                found = false;
                virualCallsList = new MethodUsageInfo(method);
                Methods.Add(method, virualCallsList);
            }

            if (method.IsVirtual) {
                if (!isVirt) {
                    virualCallsList.RegisterNonVirtualUsage();
                }
                else {
                    virualCallsList.AddVirtualUsageType(targetType);
                }
            }

            return !found;
        }

        private void AddField(FieldReference field) {
            if (field == null || IsIgnored(field)) {
                return;
            }

            AddType(field.FieldType);
            AddType(field.DeclaringType);
            FieldDefinition resolvedField = field.Resolve();

            if (resolvedField != null) {
                Fields.Add(resolvedField);
            }
        }

        private bool IsMemberWhiteListed(MemberReference member) {
            if (WhiteListCache != null)
            {
                if (WhiteListCache.Any(regex => regex.IsMatch(member.FullName))) {
                    return true;
                }
            }

            IEnumerable<CustomAttribute> customAttributes = null;

            if (member is TypeReference)
            {
                TypeDefinition type = ((TypeReference)member).Resolve();
                if (type != null)
                {
                    customAttributes = type.CustomAttributes;
                }
            }
            else if (member is MethodReference)
            {
                MethodDefinition method = ((MethodReference)member).Resolve();
                if (method != null)
                {
                    customAttributes = method.CustomAttributes;
                }
            }
            else if (member is FieldReference)
            {
                FieldDefinition field = ((FieldReference)member).Resolve();
                if (field != null)
                {
                    customAttributes = field.CustomAttributes;
                }
            }

            if (customAttributes != null && customAttributes.Any(item => item.AttributeType.Name == "JSDeadCodeEleminationEntryPoint"))
            {
                return true;
            }

            TypeReference declaringType = member.DeclaringType;

            if (declaringType != null)
            {
                var declaringTypeDefenition = declaringType.Resolve();
                if (declaringTypeDefenition != null && declaringTypeDefenition.CustomAttributes.Any(item => item.AttributeType.Name == "JSDeadCodeEleminationClassEntryPoint"))
                {
                    return true;
                }

                while (declaringTypeDefenition != null)
                {
                    if (declaringTypeDefenition.CustomAttributes.Any(
                            item => item.AttributeType.Name == "JSDeadCodeEleminationHierarchyEntryPoint"))
                    {
                        return true;
                    }

                    declaringTypeDefenition = declaringTypeDefenition.BaseType != null
                                                  ? declaringTypeDefenition.BaseType.Resolve()
                                                  : null;
                }
            }

            return false;
        }

        private void ProcessWhiteList(MemberReference member) {
            if (member is TypeReference) {
                TypeDefinition type = (member as TypeReference).Resolve();
                
                if (type != null) {
                    if (IsMemberWhiteListed(type)) {
                        AddType(type);
                    }

                    if (type.HasNestedTypes) {
                        foreach (var nestedType in type.NestedTypes) {
                            ProcessWhiteList(nestedType);
                        }
                    }

                    if (type.HasMethods) {
                        foreach (var method in type.Methods) {
                            if (IsMemberWhiteListed(method)) {
                                ProcessWhiteList(method);
                            }
                        }
                    }

                    if (type.HasFields)
                    {
                        foreach (var field in type.Fields)
                        {
                            if (IsMemberWhiteListed(field)) {
                                ProcessWhiteList(field);
                            }
                        }
                    }
                }

                return;
            }

            if (member is MethodReference) {
                if (IsMemberWhiteListed(member)) {
                    var definition = member as MethodDefinition;
                    if (definition.IsVirtual) {
                        WalkMethod(definition, null, true);
                    }
                    else {
                        WalkMethod(definition);
                    }
                }

                return;
            }

            if (member is FieldReference) {
                if (IsMemberWhiteListed(member)) {
                    AddField(member as FieldReference);
                }

                return;
            }

            throw new ArgumentException("Unexpected member reference type");
        }

        private void GetAllOverrides(MethodDefinition method, HashSet<MethodDefinition> deepOverrides) {
            if (method == null) {
                return;
            }

            HashSet<MethodDefinition> overrides = TypeMapStep.Annotations.GetOverrides(method);

            if (overrides == null) {
                return;
            }

            deepOverrides.UnionWith(overrides);
            foreach (MethodDefinition overrideMethod in overrides) {
                GetAllOverrides(overrideMethod, deepOverrides);
            }
        }
    }
}