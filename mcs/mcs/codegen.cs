//
// codegen.cs: The code generator
//
// Author:
//   Miguel de Icaza (miguel@ximian.com)
//
// (C) 2001 Ximian, Inc.
//

using System;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics.SymbolStore;

namespace Mono.CSharp {

	/// <summary>
	///    Code generator class.
	/// </summary>
	public class CodeGen {
		static AppDomain current_domain;
		static AssemblyBuilder assembly_builder;
		static ModuleBuilder   module_builder;

		static public ISymbolWriter SymbolWriter;

		public static string Basename (string name)
		{
			int pos = name.LastIndexOf ("/");

			if (pos != -1)
				return name.Substring (pos + 1);

			pos = name.LastIndexOf ("\\");
			if (pos != -1)
				return name.Substring (pos + 1);

			return name;
		}

		static string TrimExt (string name)
		{
			int pos = name.LastIndexOf (".");

			return name.Substring (0, pos);
		}

		static public string FileName;

		//
		// This routine initializes the Mono runtime SymbolWriter.
		//
		static void InitMonoSymbolWriter (string basename)
		{
			string symbol_output = basename + "-debug.s";

			//
			// Mono's default symbol writer ignores the first and third argument
			// of this method.
			//
			SymbolWriter.Initialize (new IntPtr (0), symbol_output, true);
		}

		//
		// Initializes the symbol writer
		//
		static void InitializeSymbolWriter (string basename)
		{
			SymbolWriter = module_builder.GetSymWriter ();

			//
			// If we got an ISymbolWriter instance, initialize it.
			//
			if (SymbolWriter == null)
				return;
			
			//
			// Due to lacking documentation about the first argument of the
			// Initialize method, we cannot use Microsoft's default symbol
			// writer yet.
			//
			// If we're using the mono symbol writer, the SymbolWriter object
			// is of type MonoSymbolWriter - but that's defined in a dynamically
			// loaded DLL - that's why we're doing a comparision based on the type
			// name here instead of using `SymbolWriter is MonoSymbolWriter'.
			//
			Type sym_type = ((object) SymbolWriter).GetType ();
			
			switch (sym_type.Name){
			case "MonoSymbolWriter":
				InitMonoSymbolWriter (basename);
				break;

			default:
				Report.Error (
					-18, "Cannot generate debugging information on this platform");
				break;
			}
		}

		//
		// Initializes the code generator variables
		//
		static public void Init (string name, string output, bool want_debugging_support)
		{
			AssemblyName an;

			FileName = output;
			an = new AssemblyName ();
			an.Name = TrimExt (Basename (name));
			current_domain = AppDomain.CurrentDomain;
			assembly_builder = current_domain.DefineDynamicAssembly (
				an, AssemblyBuilderAccess.RunAndSave);

			//
			// Pass a path-less name to DefineDynamicModule.  Wonder how
			// this copes with output in different directories then.
			// FIXME: figure out how this copes with --output /tmp/blah
			//
			// If the third argument is true, the ModuleBuilder will dynamically
			// load the default symbol writer.
			//
			module_builder = assembly_builder.DefineDynamicModule (
				Basename (name), Basename (output), want_debugging_support);

			if (want_debugging_support)
				InitializeSymbolWriter (an.Name);
		}

		static public AssemblyBuilder AssemblyBuilder {
			get {
				return assembly_builder;
			}
		}
		
		static public ModuleBuilder ModuleBuilder {
			get {
				return module_builder;
			}
		}

		static public void Save (string name)
		{
			try {
				assembly_builder.Save (Basename (name));
			} catch (System.IO.IOException io){
				Report.Error (16, "Coult not write to file `"+name+"', cause: " + io.Message);
			}
		}

		static public void SaveSymbols ()
		{
			if (SymbolWriter != null) {
				// If we have a symbol writer, call its Close() method to write
				// the symbol file to disk.
				//
				// When using Mono's default symbol writer, the Close() method must
				// be called after the assembly has already been written to disk since
				// it opens the assembly and reads its metadata.
				SymbolWriter.Close ();
			}
		}
	}

	/// <summary>
	///   An Emit Context is created for each body of code (from methods,
	///   properties bodies, indexer bodies or constructor bodies)
	/// </summary>
	public class EmitContext {
		public DeclSpace DeclSpace;
		public TypeContainer TypeContainer;
		public ILGenerator   ig;

		/// <summary>
		///   This variable tracks the `checked' state of the compilation,
		///   it controls whether we should generate code that does overflow
		///   checking, or if we generate code that ignores overflows.
		///
		///   The default setting comes from the command line option to generate
		///   checked or unchecked code plus any source code changes using the
		///   checked/unchecked statements or expressions.   Contrast this with
		///   the ConstantCheckState flag.
		/// </summary>
		
		public bool CheckState;

		/// <summary>
		///   The constant check state is always set to `true' and cant be changed
		///   from the command line.  The source code can change this setting with
		///   the `checked' and `unchecked' statements and expressions. 
		/// </summary>
		public bool ConstantCheckState;

		/// <summary>
		///   Whether we are emitting code inside a static or instance method
		/// </summary>
		public bool IsStatic;

		/// <summary>
		///   The value that is allowed to be returned or NULL if there is no
		///   return type.
		/// </summary>
		public Type ReturnType;

		/// <summary>
		///   Points to the Type (extracted from the TypeContainer) that
		///   declares this body of code
		/// </summary>
		public Type ContainerType;
		
		/// <summary>
		///   Whether this is generating code for a constructor
		/// </summary>
		public bool IsConstructor;
		
		/// <summary>
		///   Keeps track of the Type to LocalBuilder temporary storage created
		///   to store structures (used to compute the address of the structure
		///   value on structure method invocations)
		/// </summary>
		public Hashtable temporary_storage;

		public Block CurrentBlock;

		/// <summary>
		///   The location where we store the return value.
		/// </summary>
		LocalBuilder return_value;

		/// <summary>
		///   The location where return has to jump to return the
		///   value
		/// </summary>
		public Label ReturnLabel;

		/// <summary>
		///   Whether we are in a Finally block
		/// </summary>
		public bool InFinally;

		/// <summary>
		///   Whether we are in a Try block
		/// </summary>
		public bool InTry;

		/// <summary>
		///   Whether we are in a Catch block
		/// </summary>
		public bool InCatch;

		/// <summary>
		///  Whether we are inside an unsafe block
		/// </summary>
		public bool InUnsafe;
		
		/// <summary>
		///   Location for this EmitContext
		/// </summary>
		public Location loc;

		/// <summary>
		///   Used to "flag" the resolution process to only lookup types,
		///   and nothing else.  This is an out-of-band communication
		///   path to SimpleName from the cast operation.
		/// </summary>
		public bool OnlyLookupTypes;
		
		public EmitContext (TypeContainer parent, DeclSpace ds, Location l, ILGenerator ig,
				    Type return_type, int code_flags, bool is_constructor)
		{
			this.ig = ig;

			TypeContainer = parent;
			DeclSpace = ds;
			CheckState = RootContext.Checked;
			ConstantCheckState = true;
			
			IsStatic = (code_flags & Modifiers.STATIC) != 0;
			ReturnType = return_type;
			IsConstructor = is_constructor;
			CurrentBlock = null;
			ContainerType = parent.TypeBuilder;
			InUnsafe = ((parent.ModFlags | code_flags) & Modifiers.UNSAFE) != 0;
			OnlyLookupTypes = false;
			loc = l;
			
			if (ReturnType == TypeManager.void_type)
				ReturnType = null;
		}

		public EmitContext (TypeContainer tc, Location l, ILGenerator ig,
				    Type return_type, int code_flags, bool is_constructor)
			: this (tc, tc, l, ig, return_type, code_flags, is_constructor)
		{
		}

		public EmitContext (TypeContainer tc, Location l, ILGenerator ig,
				    Type return_type, int code_flags)
			: this (tc, tc, l, ig, return_type, code_flags, false)
		{
		}

		public void EmitTopBlock (Block block, Location loc)
		{
			bool has_ret = false;

//			Console.WriteLine ("Emitting: " + loc);

			if (CodeGen.SymbolWriter != null)
				Mark (loc);

			if (block != null){
				int errors = Report.Errors;

				block.EmitMeta (this, block);

				if (Report.Errors == errors){
					has_ret = block.Emit (this);

					if (Report.Errors == errors){
						if (RootContext.WarningLevel >= 3)
							block.UsageWarning ();
					}
				}
			}

			if (ReturnType != null && !has_ret){
				//
				// FIXME: we need full flow analysis to implement this
				// correctly and emit an error instead of a warning.
				//
				//
				Report.Error (161, loc, "Not all code paths return a value");
				return;
			}

			if (return_value != null){
				ig.MarkLabel (ReturnLabel);
				ig.Emit (OpCodes.Ldloc, return_value);
				ig.Emit (OpCodes.Ret);
			} else {
				if (!has_ret){
					if (!InTry)
						ig.Emit (OpCodes.Ret);
				}
			}
		}

		/// <summary>
		///   This is called immediately before emitting an IL opcode to tell the symbol
		///   writer to which source line this opcode belongs.
		/// </summary>
		public void Mark (Location loc)
		{
			if (!Location.IsNull (loc)) {
				ISymbolDocumentWriter doc = loc.SymbolDocument;

				if (doc != null)
					ig.MarkSequencePoint (doc, loc.Row, 0,  loc.Row, 0);
			}		}


		/// <summary>
		///   Returns a temporary storage for a variable of type t as 
		///   a local variable in the current body.
		/// </summary>
		public LocalBuilder GetTemporaryStorage (Type t)
		{
			LocalBuilder location;
			
			if (temporary_storage != null){
				location = (LocalBuilder) temporary_storage [t];
				if (location != null)
					return location;
			}
			
			location = ig.DeclareLocal (t);
			
			return location;
		}

		public void FreeTemporaryStorage (LocalBuilder b)
		{
			// Empty for now.
		}

		/// <summary>
		///   Current loop begin and end labels.
		/// </summary>
		public Label LoopBegin, LoopEnd;

		/// <summary>
		///   Whether we are inside a loop and break/continue are possible.
		/// </summary>
		public bool  InLoop;

		/// <summary>
		///   Default target in a switch statement.   Only valid if
		///   InSwitch is true
		/// </summary>
		public Label DefaultTarget;

		/// <summary>
		///   If this is non-null, points to the current switch statement
		/// </summary>
		public Switch Switch;

		/// <summary>
		///   ReturnValue creates on demand the LocalBuilder for the
		///   return value from the function.  By default this is not
		///   used.  This is only required when returns are found inside
		///   Try or Catch statements.
		/// </summary>
		public LocalBuilder TemporaryReturn ()
		{
			if (return_value == null){
				return_value = ig.DeclareLocal (ReturnType);
				ReturnLabel = ig.DefineLabel ();
			}

			return return_value;
		}

		/// <summary>
	        ///   A dynamic This that is shared by all variables in a emitcontext.
		///   Created on demand.
		/// </summary>
		public Expression my_this;
		public Expression This {
			get {
				if (my_this == null)
					my_this = new This (loc).Resolve (this);

				return my_this;
			}
		}
	}
}
