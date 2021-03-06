//
// Generator.cs: C++ Interop Code Generator
//
//

using System;
using System.IO;
using System.Collections.Generic;
using System.Xml;
using System.Linq;
using System.Reflection;
using System.CodeDom;
using System.CodeDom.Compiler;
using Microsoft.CSharp;

using NDesk.Options;

using Mono.VisualC.Interop;

public class Generator
{
	// Command line arguments
	string OutputDir;
	string Namespace;
	string LibBaseName;
	string InputFileName;
	string FilterFile;

	CodeDomProvider Provider;
	CodeGeneratorOptions CodeGenOptions;

	// Classes to generate code for
	Dictionary<string, string> Filters;

	public static int Main (String[] args) {
		var generator = new Generator ();
		generator.Run (args);
		return 0;
	}

	int ParseArguments (String[] args) {
		bool help = false;

		var p = new OptionSet {
				{ "h|?|help", "Show this help message", v => help = v != null },
				{ "o=", "Set the output directory", v => OutputDir = v },
				{ "ns=|namespace=", "Set the namespace of the generated code", v => Namespace = v },
				{ "lib=", "The base name of the C++ library, i.e. 'qt' for libqt.so", v =>LibBaseName = v },
				{ "filters=", "A file containing filter directives for filtering classes", v => FilterFile = v }
			};

		try {
			args = p.Parse (args).ToArray ();
		} catch (OptionException) {
			Console.WriteLine ("Try `generator --help' for more information.");
			return 1;
		}

		if (help) {
			p.WriteOptionDescriptions (Console.Error);
			return 1;
		}

		if (args.Length != 1) {
			Console.WriteLine ("Usage: generator <options> <input xml file>");
			return 1;
		}

		InputFileName = args [0];

		if (LibBaseName == null) {
			Console.WriteLine ("The --lib= option is required.");
			return 1;
		}

		if (OutputDir == null)
			OutputDir = "output";

		return 0;
	}

	void Run (String[] args) {
		if (ParseArguments (args) != 0) {
			Environment.Exit (1);
		}

		Node root = LoadXml (InputFileName);

		if (FilterFile != null)
			LoadFilters (FilterFile);

		CreateClasses (root);

		CreateMethods ();

		GenerateCode ();
	}

	Node LoadXml (string file) {
		XmlReader reader = XmlReader.Create (file, new XmlReaderSettings ());

		Node[] parents = new Node [1024];

		Node root = null;

		while (reader.Read()) {
			if (reader.IsStartElement ()) {
				string type = reader.Name;

				var attributes = new Dictionary<string, string> ();
				while (reader.MoveToNextAttribute ()) {
					attributes [reader.Name] = reader.Value;
				}

				Node n = new Node {
						Id = "",
						Type = type,
						Attributes = attributes,
						Children = new List<Node> ()
				};

				if (attributes.ContainsKey ("id")) {
					n.Id = attributes ["id"];
					Node.IdToNode [n.Id] = n;
				}

				if (attributes.ContainsKey ("name"))
					n.Name = attributes ["name"];

				if (parents [reader.Depth - 1] != null) {
					//Console.WriteLine (parents [reader.Depth - 1].type + " -> " + e.type);
					parents [reader.Depth - 1].Children.Add (n);
				}
				parents [reader.Depth] = n;

				if (n.Type == "GCC_XML" && root == null)
					root = n;
			}
		}

		return root;
	}

	void LoadFilters (string file) {
		Filters = new Dictionary <string, string> ();
		foreach (string s in File.ReadAllLines (file)) {
			Filters [s] = s;
		}
	}

	List<Class> Classes;
	Dictionary<Node, Class> NodeToClass;

	void CreateClasses (Node root) {
		List<Node> classNodes = root.Children.Where (o => o.Type == "Class" || o.Type == "Struct").ToList ();
		classNodes.RemoveAll (o => o.IsTrue ("incomplete") || !o.HasValue ("name"));

		if (Filters != null)
			classNodes.RemoveAll (o => !Filters.ContainsKey (o.Name));

		List<Class> classes = new List<Class> ();
		NodeToClass = new Dictionary <Node, Class> ();

		foreach (Node n in classNodes) {
			Console.WriteLine (n.Name);

			Class klass = new Class (n);
			classes.Add (klass);
			NodeToClass [n] = klass;
		}

		// Compute bases
		foreach (Class klass in classes) {
			foreach (Node bn in klass.Node.Children.Where (o => o.Type == "Base")) {
				Class baseClass = NodeToClass [bn.NodeForAttr ("type")];
				klass.BaseClasses.Add (baseClass);
			}
		}

		Classes = classes;
	}

	void CreateMethods () {
		foreach (Class klass in Classes) {
			if (!klass.Node.HasValue ("members"))
				continue;

			List<Node> members = new List<Node> ();
			foreach (string id in klass.Node ["members"].Split (' ')) {
				if (id == "")
					continue;
				members.Add (Node.IdToNode [id]);
			}

			int fieldCount = 0;
			foreach (Node n in members) {
				bool ctor = false;
				bool dtor = false;

				switch (n.Type) {
				case "Field":
					CppType fieldType = GetType (GetTypeNode (n));
					string fieldName;
					if (n.Name != "")
						fieldName = n.Name;
					else
						fieldName = "field" + fieldCount++;
					if (CppTypeToCodeDomType (fieldType) == null)
						// FIXME: Assume IntPtr
						fieldType = ((CppType)CppTypes.Void).Modify (CppModifiers.Pointer);
					klass.Fields.Add (new Field (fieldName, fieldType));
					break;
				case "Constructor":
					ctor = true;
					break;
				case "Destructor":
					dtor = true;
					break;
				case "Method":
					break;
				default:
					continue;
				}

				if (!n.CheckValue ("access", "public") || (n.HasValue ("overrides") && !dtor))
					continue;

				if (!n.IsTrue ("extern") && !n.IsTrue ("inline"))
					continue;

				string name = dtor ? "Destruct" : n.Name;

				var method = new Method (n) {
						Name = name,
						IsVirtual = n.IsTrue ("virtual"),
						IsStatic = n.IsTrue ("static"),
						IsConst = n.IsTrue ("const"),
						IsInline = n.IsTrue ("inline"),
						IsArtificial = n.IsTrue ("artificial"),
						IsConstructor = ctor,
						IsDestructor = dtor
				};

				bool skip = false;

				CppType retType;
				if (n.HasValue ("returns"))
					retType = GetType (n.NodeForAttr ("returns"));
				else
					retType = CppTypes.Void;
				if (retType.ElementType == CppTypes.Unknown)
					skip = true;
				if (CppTypeToCodeDomType (retType) == null) {
					//Console.WriteLine ("\t\tS: " + retType);
					skip = true;
				}

				method.ReturnType = retType;

				int c = 0;
				List<CppType> argTypes = new List<CppType> ();
				foreach (Node arg in n.Children.Where (o => o.Type == "Argument")) {
					string argname;
					if (arg.Name == null || arg.Name == "")
						argname = "arg" + c;
					else
						argname = arg.Name;

					CppType argtype = GetType (GetTypeNode (arg));
					if (argtype.ElementType == CppTypes.Unknown) {
						//Console.WriteLine ("Skipping method " + klass.Name + "::" + member.Name + " () because it has an argument with unknown type '" + TypeNodeToString (arg) + "'.");
						skip = true;
					}

					if (CppTypeToCodeDomType (argtype) == null) {
						//Console.WriteLine ("\t\tS: " + argtype);
						skip = true;
					}

					method.Parameters.Add (new Parameter (argname, argtype));
					argTypes.Add (argtype);

					c++;
				}
				if (skip)
					continue;

				// FIXME: More complete type name check
				if (ctor && argTypes.Count == 1 && argTypes [0].ElementType == CppTypes.Class && argTypes [0].ElementTypeName == klass.Name && argTypes [0].Modifiers.Count == 2 && argTypes [0].Modifiers.Contains (CppModifiers.Const) && argTypes [0].Modifiers.Contains (CppModifiers.Reference))
					method.IsCopyCtor = true;
				
				Console.WriteLine ("\t" + klass.Name + "." + method.Name);

				klass.Methods.Add (method);
			}

			foreach (Method method in klass.Methods) {
				if (AddAsQtProperty (klass, method))
					method.GenWrapperMethod = false;
			}

			Field f2 = klass.Fields.FirstOrDefault (f => f.Type.ElementType == CppTypes.Unknown);
			if (f2 != null) {
				Console.WriteLine ("Skipping " + klass.Name + " because field " + f2.Name + " has unknown type.");
				klass.Disable = true;
			}
		}
	}

	//
	// Property support
	// This is QT specific
	//
    bool AddAsQtProperty (Class klass, Method method) {
		// if it's const, returns a value, has no parameters, and there is no other method with the same name
		//  in this class assume it's a property getter (for now?)
		if (method.IsConst && !method.ReturnType.Equals (CppTypes.Void) && !method.Parameters.Any () &&
			klass.Methods.Where (o => o.Name == method.Name).FirstOrDefault () == method) {
			Property property;

			property = klass.Properties.Where (o => o.Name == method.FormattedName).FirstOrDefault ();
			if (property != null) {
				property.GetMethod = method;
			} else {
				property = new Property (method.FormattedName, method.ReturnType) { GetMethod = method };
				klass.Properties.Add (property);
			}

			return true;
		}

		// if it's name starts with "set", does not return a value, and has one arg (besides this ptr)
		// and there is no other method with the same name...
		if (method.Name.ToLower ().StartsWith ("set") && method.ReturnType.Equals (CppTypes.Void) &&
			method.Parameters.Count == 1 && klass.Methods.Where (o => o.Name == method.Name).Count () == 1) {
			string getterName = method.Name.Substring (3).TrimStart ('_').ToLower ();

			string pname = method.FormattedName.Substring (3);
			Property property = null;

			// ...AND there is a corresponding getter method that returns the right type, then assume it's a property setter
			bool doIt = false;
			property = klass.Properties.Where (o => o.Name == pname).FirstOrDefault ();
			if (property != null) {
				doIt = property.GetMethod != null && property.GetMethod.ReturnType.Equals (method.Parameters[0].Type);
			} else {
				Method getter = klass.Methods.Where (o => o.Name == getterName).FirstOrDefault ();
				doIt = getter != null && getter.ReturnType.Equals (method.Parameters[0].Type);
			}
			if (doIt) {
				if (property != null) {
					property.SetMethod = method;
				} else {
					property = new Property (pname, method.Parameters [0].Type) { SetMethod = method };
					klass.Properties.Add (property);
				}

				// set the method's arg name to "value" so that the prop setter works right
				var valueParam = method.Parameters[0];
				valueParam.Name = "value";

				return true;
			}
		}

		return false;
	}

	// Return a CppType for the type node N, return CppTypes.Unknown for unknown types
	CppType GetType (Node n) {
		return GetType (n, new CppType ());
	}

	CppType GetType (Node n, CppType modifiers) {
		switch (n.Type) {
		case "ArrayType":
			return GetType (GetTypeNode (n), modifiers.Modify (CppModifiers.Array));
		case "PointerType":
			return GetType (GetTypeNode (n), modifiers.Modify (CppModifiers.Pointer));
		case "ReferenceType":
			return GetType (GetTypeNode (n), modifiers.Modify (CppModifiers.Reference));
		case "FundamentalType":
			return modifiers.CopyTypeFrom (new CppType (n.Name));
		case "CvQualifiedType":
			if (n.IsTrue ("const"))
				return GetType (GetTypeNode (n), modifiers.Modify (CppModifiers.Const));
			else
				throw new NotImplementedException ();
		case "Class":
		case "Struct":
			if (!NodeToClass.ContainsKey (n)) {
				if (modifiers.Modifiers.Count () == 1 && modifiers.Modifiers [0] == CppModifiers.Pointer)
					// Map these to void*
					return modifiers.CopyTypeFrom (CppTypes.Void);
				else
					return CppTypes.Unknown;
			}
			return modifiers.CopyTypeFrom (new CppType (CppTypes.Class, NodeToClass [n].Name));
		default:
			return CppTypes.Unknown;
		}
	}

	Node GetTypeNode (Node n) {
		return Node.IdToNode [n.Attributes ["type"]];
	}

	// Return the CodeDom type reference corresponding to T, or null
	public CodeTypeReference CppTypeToCodeDomType (CppType t, out bool byref) {
		CodeTypeReference rtype = null;

		byref = false;
		Type mtype = t.ToManagedType ();
		if (mtype != null) {
			if (mtype.IsByRef) {
				byref = true;
				mtype = mtype.GetElementType ();
			}
			return new CodeTypeReference (mtype);
		}

		if (t.Modifiers.Count > 0 && t.ElementType != CppTypes.Void && t.ElementType != CppTypes.Class)
			return null;
		switch (t.ElementType) {
		case CppTypes.Void:
			if (t.Modifiers.Count > 0) {
				if (t.Modifiers.Count == 1 && t.Modifiers [0] == CppModifiers.Pointer)
					rtype = new CodeTypeReference (typeof (IntPtr));
				else
					return null;
			} else {
				rtype = new CodeTypeReference (typeof (void));
			}
			break;
		case CppTypes.Bool:
			rtype = new CodeTypeReference (typeof (bool));
			break;
		case CppTypes.Int:
			rtype = new CodeTypeReference (typeof (int));
			break;
		case CppTypes.Float:
			rtype = new CodeTypeReference (typeof (float));
			break;
		case CppTypes.Double:
			rtype = new CodeTypeReference (typeof (double));
			break;
		case CppTypes.Char:
			rtype = new CodeTypeReference (typeof (char));
			break;
		case CppTypes.Class:
			// FIXME: Full name
			rtype = new CodeTypeReference (t.ElementTypeName);
			break;
		default:
			return null;
		}
		return rtype;
	}

	public CodeTypeReference CppTypeToCodeDomType (CppType t) {
		bool byref;
		
		return CppTypeToCodeDomType (t, out byref);
	}

	void GenerateCode () {
		Directory.CreateDirectory (OutputDir);

		Provider = new CSharpCodeProvider ();
		CodeGenOptions = new CodeGeneratorOptions { BlankLinesBetweenMembers = false };

		CodeTypeDeclaration libDecl = null;

		// Generate Libs class
		{
			var cu = new CodeCompileUnit ();
			var ns = new CodeNamespace (Namespace);
			ns.Imports.Add(new CodeNamespaceImport("System"));
			ns.Imports.Add(new CodeNamespaceImport("Mono.VisualC.Interop"));
			cu.Namespaces.Add (ns);

			var decl = new CodeTypeDeclaration ("Libs");

			var field = new CodeMemberField (new CodeTypeReference ("CppLibrary"), LibBaseName);
			field.Attributes = MemberAttributes.Public|MemberAttributes.Static;
			field.InitExpression = new CodeObjectCreateExpression (new CodeTypeReference ("CppLibrary"), new CodeExpression [] { new CodePrimitiveExpression (LibBaseName) });
			decl.Members.Add (field);

			ns.Types.Add (decl);

			libDecl = decl;

			//Provider.GenerateCodeFromCompileUnit(cu, Console.Out, CodeGenOptions);
			using (TextWriter w = File.CreateText (Path.Combine (OutputDir, "Libs.cs"))) {
				Provider.GenerateCodeFromCompileUnit(cu, w, CodeGenOptions);
			}
		}

		// Generate user classes
		foreach (Class klass in Classes) {
			if (klass.Disable)
				continue;

			var cu = new CodeCompileUnit ();
			var ns = new CodeNamespace (Namespace);
			ns.Imports.Add(new CodeNamespaceImport("System"));
			ns.Imports.Add(new CodeNamespaceImport("Mono.VisualC.Interop"));
			cu.Namespaces.Add (ns);

			ns.Types.Add (klass.GenerateClass (this, libDecl, LibBaseName));

			//Provider.GenerateCodeFromCompileUnit(cu, Console.Out, CodeGenOptions);
			using (TextWriter w = File.CreateText (Path.Combine (OutputDir, klass.Name + ".cs"))) {
				// These are reported for the fields of the native layout structures
				Provider.GenerateCodeFromCompileUnit (new CodeSnippetCompileUnit("#pragma warning disable 0414, 0169"), w, CodeGenOptions);
				Provider.GenerateCodeFromCompileUnit(cu, w, CodeGenOptions);
			}
		}
	}
}
