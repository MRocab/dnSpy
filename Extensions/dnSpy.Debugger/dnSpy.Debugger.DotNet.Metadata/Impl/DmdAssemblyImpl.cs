﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace dnSpy.Debugger.DotNet.Metadata.Impl {
	sealed class DmdAssemblyImpl : DmdAssembly {
		internal sealed override void YouCantDeriveFromThisClass() => throw new InvalidOperationException();
		public override DmdAppDomain AppDomain => appDomain;
		public override string Location { get; }
		public override string ImageRuntimeVersion => metadataReader.ImageRuntimeVersion;
		public override DmdMethodInfo EntryPoint => metadataReader.EntryPoint;

		internal string ApproximateSimpleName {
			get {
				if (approximateSimpleName == null)
					approximateSimpleName = CalculateApproximateSimpleName();
				return approximateSimpleName;
			}
		}
		string approximateSimpleName;
		string CalculateApproximateSimpleName() {
			if (IsInMemory || IsDynamic)
				return string.Empty;
			try {
				var res = Path.GetFileNameWithoutExtension(Location);
				if (res.EndsWith(".ni"))
					return Path.GetFileNameWithoutExtension(res);
				return res;
			}
			catch (ArgumentException) {
			}
			return string.Empty;
		}

		public override DmdModule ManifestModule {
			get {
				lock (LockObject)
					return modules.Count == 0 ? null : modules[0];
			}
		}

		internal DmdAppDomainImpl AppDomainImpl => appDomain;
		readonly DmdAppDomainImpl appDomain;
		readonly List<DmdModuleImpl> modules;
		readonly DmdMetadataReader metadataReader;

		public DmdAssemblyImpl(DmdAppDomainImpl appDomain, DmdMetadataReader metadataReader, string location) {
			modules = new List<DmdModuleImpl>();
			this.appDomain = appDomain ?? throw new ArgumentNullException(nameof(appDomain));
			this.metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
			Location = location ?? throw new ArgumentNullException(nameof(location));
		}

		internal void Add(DmdModuleImpl module) {
			if (module == null)
				throw new ArgumentNullException(nameof(module));
			lock (LockObject) {
				Debug.Assert(!modules.Contains(module));
				modules.Add(module);
			}
		}

		internal void Remove(DmdModuleImpl module) {
			if (module == null)
				throw new ArgumentNullException(nameof(module));
			lock (LockObject) {
				bool b = modules.Remove(module);
				Debug.Assert(b);
			}
		}

		public override DmdModule[] GetModules() => GetLoadedModules();
		public override DmdModule[] GetLoadedModules() {
			lock (LockObject)
				return modules.ToArray();
		}

		public override DmdModule GetModule(string name) {
			if (name == null)
				throw new ArgumentNullException(nameof(name));
			lock (LockObject) {
				foreach (var module in modules) {
					// This is case insensitive, see AssemblyNative::GetModule(pAssembly,wszFileName,retModule) in coreclr/src/vm/assemblynative.cpp
					if (StringComparer.OrdinalIgnoreCase.Equals(module.ScopeName, name))
						return module;
				}
			}
			return null;
		}

		public override DmdAssemblyName GetName() {
			if (asmName == null) {
				var newAsmName = metadataReader.GetName();
				newAsmName.Flags |= DmdAssemblyNameFlags.PublicKey;
				if (metadataReader.MDStreamVersion >= 0x00010000) {
					metadataReader.GetPEKind(out var peKind, out var machine);
					if ((newAsmName.Flags & DmdAssemblyNameFlags.PA_FullMask) == DmdAssemblyNameFlags.PA_NoPlatform)
						newAsmName.Flags = (newAsmName.Flags & ~DmdAssemblyNameFlags.PA_FullMask) | DmdAssemblyNameFlags.PA_None;
					else
						newAsmName.Flags = (newAsmName.Flags & ~DmdAssemblyNameFlags.PA_FullMask) | GetProcessorArchitecture(peKind, machine);
				}
				asmName = newAsmName;
			}
			return asmName.Clone();
		}
		DmdAssemblyName asmName;

		static DmdAssemblyNameFlags GetProcessorArchitecture(DmdPortableExecutableKinds peKind, DmdImageFileMachine machine) {
			if ((peKind & DmdPortableExecutableKinds.PE32Plus) == 0) {
				switch (machine) {
				case DmdImageFileMachine.I386:
					if ((peKind & DmdPortableExecutableKinds.Required32Bit) != 0)
						return DmdAssemblyNameFlags.PA_x86;
					if ((peKind & DmdPortableExecutableKinds.ILOnly) != 0)
						return DmdAssemblyNameFlags.PA_MSIL;
					return DmdAssemblyNameFlags.PA_x86;

				case DmdImageFileMachine.ARM:
					return DmdAssemblyNameFlags.PA_ARM;
				}
			}
			else {
				switch (machine) {
				case DmdImageFileMachine.I386:
					if ((peKind & DmdPortableExecutableKinds.ILOnly) != 0)
						return DmdAssemblyNameFlags.PA_MSIL;
					break;

				case DmdImageFileMachine.AMD64:
					return DmdAssemblyNameFlags.PA_AMD64;

				case DmdImageFileMachine.IA64:
					return DmdAssemblyNameFlags.PA_IA64;
				}
			}

			return DmdAssemblyNameFlags.PA_None;
		}

		public override DmdType[] GetExportedTypes() {
			var list = new List<DmdType>();
			foreach (var type in metadataReader.GetTypes()) {
				if (type.IsVisible)
					list.Add(type);
			}
			foreach (var type in metadataReader.GetExportedTypes()) {
				if (!IsTypeForwarder(type))
					list.Add(type);
			}
			return list.ToArray();
		}

		static bool IsTypeForwarder(DmdType type) {
			var nonNested = DmdTypeUtilities.GetNonNestedType(type);
			if ((object)nonNested == null)
				return false;
			return nonNested.TypeScope.Kind == DmdTypeScopeKind.AssemblyRef;
		}

		public override DmdAssemblyName[] GetReferencedAssemblies() => metadataReader.GetReferencedAssemblies();
		internal DmdTypeDef GetType(DmdTypeRef typeRef, bool ignoreCase) => appDomain.TryLookup(this, typeRef, ignoreCase);

		sealed class TypeDefResolver : ITypeDefResolver {
			readonly DmdAssemblyImpl assembly;
			readonly bool ignoreCase;

			public TypeDefResolver(DmdAssemblyImpl assembly, bool ignoreCase) {
				this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
				this.ignoreCase = ignoreCase;
			}

			public DmdTypeDef GetTypeDef(DmdAssemblyName assemblyName, List<string> typeNames) {
				if (typeNames.Count == 0)
					return null;

				if (assemblyName != null && !AssemblyNameEqualityComparer.Instance.Equals(assembly.GetName(), assemblyName))
					return null;

				DmdTypeDef type;
				DmdTypeUtilities.SplitFullName(typeNames[0], out string @namespace, out string name);

				var module = assembly.ManifestModule;
				if (module == null)
					return null;
				var typeRef = new DmdParsedTypeRef(module, null, DmdTypeScope.Invalid, @namespace, name, null);
				type = assembly.GetType(typeRef, ignoreCase);

				if ((object)type == null)
					return null;
				for (int i = 1; i < typeNames.Count; i++) {
					var flags = DmdBindingFlags.Public | DmdBindingFlags.NonPublic;
					if (ignoreCase)
						flags |= DmdBindingFlags.IgnoreCase;
					type = (DmdTypeDef)type.GetNestedType(typeNames[i], flags);
					if ((object)type == null)
						return null;
				}
				return type;
			}
		}

		public override DmdType GetType(string typeName, DmdGetTypeOptions options) {
			if (typeName == null)
				throw new ArgumentNullException(nameof(typeName));

			var resolver = new TypeDefResolver(this, (options & DmdGetTypeOptions.IgnoreCase) != 0);
			var type = DmdTypeNameParser.Parse(resolver, typeName);
			if ((object)type != null)
				return type;

			if ((options & DmdGetTypeOptions.ThrowOnError) != 0)
				throw new TypeNotFoundException(typeName);
			return null;
		}

		public override IList<DmdCustomAttributeData> GetCustomAttributesData() {
			if (customAttributes != null)
				return customAttributes;
			lock (LockObject) {
				if (customAttributes != null)
					return customAttributes;
				var cas = metadataReader.ReadCustomAttributes(0x20000001);
				customAttributes = CustomAttributesHelper.AddPseudoCustomAttributes(this, cas);
				return customAttributes;
			}
		}
		ReadOnlyCollection<DmdCustomAttributeData> customAttributes;
	}
}
