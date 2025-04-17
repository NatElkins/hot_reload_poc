> **WARNING**: This is super messy.  No guarantees I'll ever clean it up.  But for now I just wanted to show that it was possible to patch an F# assembly using F#.

To run: `DOTNET_MODIFIABLE_ASSEMBLIES=debug dotnet run --project hot_reload_poc/src/TestApp/TestApp.fsproj`

Super minimal example of hot reload in F#.  Here's a high level overview of what it does:

1. Creates an F# dll from a very simple template.  Uses FSharp.Compiler.Services to compile it to disk.
2. Loads that assembly into an AssemblyLoadContext
3. Generates a delta patch for the assembly.  This requires manually constructing metadata tables using MetadataBuilder and some hand-written IL.
4. Use the ApplyUpdate method which is exposed by the runtime to patch the assembly.
5. Invokes the running DLL with reflection to confirm it's now returning the new value.

Every patch has two required components (the IL patch and the metadata patch) and then I think the pdb might be optional.  You can read the help text of the `mdv` tool to understand it, but the basic usage is something like `mdv 0.dll '/g:1.meta;1.il' ` where 0.dll is the initial dll (Generation 0) and `1.meta` and `1.il`  comprise the first generation (Generation 1).  If you build your patches properly it'll end up looking something like this:

```
MetadataVersion: v4.0.30319

>>>
>>> Generation 0:
>>>

Module (0x00):
=======================================================================================
   Gen  Name          Mvid                                         EncId  EncBaseId  
=======================================================================================
1: 0    '0.dll' (#1)  {aeecabc8-5a3b-2ff1-b6c1-164e5acc0694} (#1)  nil    nil        

TypeRef (0x01):
===================================================================================================================================
    Scope                     Name                                         Namespace                                                
===================================================================================================================================
 1: 0x23000001 (AssemblyRef)  'Object' (#7b)                               'System' (#74)                                           
 2: 0x23000002 (AssemblyRef)  'Object' (#7b)                               'System' (#74)                                           
 3: 0x23000003 (AssemblyRef)  'FSharpInterfaceDataVersionAttribute' (#e4)  'Microsoft.FSharp.Core' (#ce)                            
 4: 0x23000002 (AssemblyRef)  'DebuggableAttribute' (#12d)                 'System.Diagnostics' (#11a)                              
 5: 0x01000004 (TypeRef)      'DebuggingModes' (#141)                      nil                                                      
 6: 0x23000003 (AssemblyRef)  'LanguagePrimitives' (#163)                  'Microsoft.FSharp.Core' (#ce)                            
 7: 0x01000006 (TypeRef)      'IntrinsicOperators' (#150)                  nil                                                      
 8: 0x23000003 (AssemblyRef)  'LowPriority' (#1a5)                         'Microsoft.FSharp.Control.TaskBuilderExtensions' (#176)  
 9: 0x23000003 (AssemblyRef)  'MediumPriority' (#1b1)                      'Microsoft.FSharp.Control.TaskBuilderExtensions' (#176)  
 a: 0x23000003 (AssemblyRef)  'HighPriority' (#1c0)                        'Microsoft.FSharp.Control.TaskBuilderExtensions' (#176)  
 b: 0x23000003 (AssemblyRef)  'LowPriority' (#1a5)                         'Microsoft.FSharp.Linq.QueryRunExtensions' (#1cd)        
 c: 0x23000003 (AssemblyRef)  'HighPriority' (#1c0)                        'Microsoft.FSharp.Linq.QueryRunExtensions' (#1cd)        
 d: 0x23000003 (AssemblyRef)  'AbstractClassAttribute' (#1ff)              'Microsoft.FSharp.Core' (#ce)                            
 e: 0x23000003 (AssemblyRef)  'SealedAttribute' (#216)                     'Microsoft.FSharp.Core' (#ce)                            
 f: 0x23000003 (AssemblyRef)  'CompilationMappingAttribute' (#226)         'Microsoft.FSharp.Core' (#ce)                            
10: 0x23000003 (AssemblyRef)  'SourceConstructFlags' (#242)                'Microsoft.FSharp.Core' (#ce)                            

TypeDef (0x02):
=======================================================================================================================================================================================================================================================
   Name                     Namespace                EnclosingType  BaseType              Interfaces  Fields  Methods                Attributes                                                                         ClassSize  PackingSize  
=======================================================================================================================================================================================================================================================
1: '<Module>' (#6b)         nil                      nil (TypeDef)  0x01000001 (TypeRef)  nil         nil     nil                    0                                                                                  n/a        n/a          
2: 'SimpleLib' (#93)        'TestApp' (#8b)          nil (TypeDef)  0x01000002 (TypeRef)  nil         nil     0x06000001-0x06000001  0x00002181 (AutoLayout, AnsiClass, Class, Public, Abstract, Sealed, Serializable)  n/a        n/a          
3: '$SimpleTest$fsx' (#bc)  '<StartupCode$0>' (#ac)  nil (TypeDef)  0x01000001 (TypeRef)  nil         nil     nil                    0x00000180 (AutoLayout, AnsiClass, Class, Abstract, Sealed)                        n/a        n/a          

Method (0x06, 0x1C):
==================================================================================================================================================================================================
   Name               Signature       RVA         Parameters  GenericParameters  Attributes                                 ImplAttributes  ImportAttributes  ImportName  ImportModule     
==================================================================================================================================================================================================
1: 'GetValue' (#1f6)  int32 () (#3a)  0x00002050  nil         nil                0x00000016 (PrivateScope, Public, Static)  0               0                 nil         nil (ModuleRef)  

MemberRef (0x0a):
==================================================================================================
   Parent                Name            Signature                                                
==================================================================================================
1: 0x01000003 (TypeRef)  '.ctor' (#114)  void (int32, int32, int32) (#13)                         
2: 0x01000004 (TypeRef)  '.ctor' (#114)  void (nil.DebuggingModes) (#2b)                          
3: 0x0100000d (TypeRef)  '.ctor' (#114)  void () (#3e)                                            
4: 0x0100000e (TypeRef)  '.ctor' (#114)  void () (#3e)                                            
5: 0x0100000f (TypeRef)  '.ctor' (#114)  void (Microsoft.FSharp.Core.SourceConstructFlags) (#47)  

CustomAttribute (0x0c):
=========================================================================================================
   Parent                 Constructor             Value                                                  
=========================================================================================================
1: 0x20000001 (Assembly)  0x0a000001 (MemberRef)  01-00-02-00-00-00-00-00-00-00-00-00-00-00-00-00 (#1a)  
2: 0x20000001 (Assembly)  0x0a000002 (MemberRef)  01-00-03-01-00-00-00-00 (#31)                          
3: 0x02000002 (TypeDef)   0x0a000003 (MemberRef)  01-00-00-00 (#42)                                      
4: 0x02000002 (TypeDef)   0x0a000004 (MemberRef)  01-00-00-00 (#42)                                      
5: 0x02000002 (TypeDef)   0x0a000005 (MemberRef)  01-00-03-00-00-00-00-00 (#4d)                          

Assembly (0x20):
========================================================================================================
   Name       Version  Culture  PublicKey  Flags                                  HashAlgorithm      
========================================================================================================
1: '0' (#cc)  0.0.0.0  nil      nil        0x00008000 (EnableJitCompileTracking)  0x00008004 (Sha1)  

AssemblyRef (0x23):
=====================================================================================
   Name                    Version   Culture  PublicKeyOrToken              Flags  
=====================================================================================
1: 'mscorlib' (#82)        4.0.0.0   nil      B7-7A-5C-56-19-34-E0-89 (#1)  0      
2: 'System.Runtime' (#9d)  10.0.0.0  nil      B0-3F-5F-7F-11-D5-0A-3A (#a)  0      
3: 'FSharp.Core' (#108)    9.0.0.0   nil      B0-3F-5F-7F-11-D5-0A-3A (#a)  0      

ManifestResource (0x28):
====================================================================================
   Name                                        Attributes  Offset  Implementation  
====================================================================================
1: 'FSharpSignatureCompressedData.0' (#7)      Public      0       nil (File)      
2: 'FSharpSignatureCompressedDataB.0' (#27)    Public      368     nil (File)      
3: 'FSharpOptimizationCompressedData.0' (#48)  Public      384     nil (File)      

#US (size = 4):
  0: ''
  1: ''
  2: ''
  3: ''

#String (size = 599):
  0: ''
  1: '0.dll'
  7: 'FSharpSignatureCompressedData.0'
  27: 'FSharpSignatureCompressedDataB.0'
  48: 'FSharpOptimizationCompressedData.0'
  6b: '<Module>'
  74: 'System'
  7b: 'Object'
  82: 'mscorlib'
  8b: 'TestApp'
  93: 'SimpleLib'
  9d: 'System.Runtime'
  ac: '<StartupCode$0>'
  bc: '$SimpleTest$fsx'
  cc: '0'
  ce: 'Microsoft.FSharp.Core'
  e4: 'FSharpInterfaceDataVersionAttribute'
  108: 'FSharp.Core'
  114: '.ctor'
  11a: 'System.Diagnostics'
  12d: 'DebuggableAttribute'
  141: 'DebuggingModes'
  150: 'IntrinsicOperators'
  163: 'LanguagePrimitives'
  176: 'Microsoft.FSharp.Control.TaskBuilderExtensions'
  1a5: 'LowPriority'
  1b1: 'MediumPriority'
  1c0: 'HighPriority'
  1cd: 'Microsoft.FSharp.Linq.QueryRunExtensions'
  1f6: 'GetValue'
  1ff: 'AbstractClassAttribute'
  216: 'SealedAttribute'
  226: 'CompilationMappingAttribute'
  242: 'SourceConstructFlags'

#Blob (size = 88):
  0: <empty>
  1 (Key): B7-7A-5C-56-19-34-E0-89
  a (Key): B0-3F-5F-7F-11-D5-0A-3A
  13 (MemberRefSignature): 20-03-01-08-08-08
  1a (CustomAttribute): 01-00-02-00-00-00-00-00-00-00-00-00-00-00-00-00
  2b (MemberRefSignature): 20-01-01-11-15
  31 (CustomAttribute): 01-00-03-01-00-00-00-00
  3a (MethodSignature): 00-00-08
  3e (MemberRefSignature): 20-00-01
  42 (CustomAttribute): 01-00-00-00
  47 (MemberRefSignature): 20-01-01-11-41
  4d (CustomAttribute): 01-00-03-00-00-00-00-00
  56: <empty>
  57: <empty>

Sizes:
  Key: 16 bytes
  MethodSignature: 3 bytes
  MemberRefSignature: 19 bytes
  CustomAttribute: 36 bytes
#Guid (size = 16):
  1: {aeecabc8-5a3b-2ff1-b6c1-164e5acc0694}

Method 'TestApp.SimpleLib.GetValue' (0x06000001)
{
  // Code size        3 (0x3)
  .maxstack  8
  IL_0000:  ldc.i4.s   42
  IL_0002:  ret
}

MetadataVersion: v4.0.30319

>>>
>>> Generation 1:
>>>

Module (0x00):
====================================================================================================================================
   Gen  Name                 Mvid                                         EncId                                        EncBaseId  
====================================================================================================================================
1: 1    '0.dll' (#270/1:19)  {aeecabc8-5a3b-2ff1-b6c1-164e5acc0694} (#2)  {4c527cf5-a25e-41c0-b73c-748af0100c21} (#3)  nil        

TypeRef (0x01):
=========================================================================
   Scope                     Name                  Namespace             
=========================================================================
1: 0x23000001 (AssemblyRef)  'Object' (#27d/1:26)  'System' (#276/1:1f)  

Method (0x06, 0x1C):
======================================================================================================================================================================================================================
   Name                    Signature           RVA         Parameters  GenericParameters  Attributes                                            ImplAttributes  ImportAttributes  ImportName  ImportModule     
======================================================================================================================================================================================================================
1: 'GetValue' (#267/1:10)  int32 () (#62/1:a)  0x00000004  n/a (EnC)   nil                0x00000096 (PrivateScope, Public, Static, HideBySig)  0               0                 nil         nil (ModuleRef)  

EnC Log (0x1e):
=======================================
   Entity                    Operation  
=======================================
1: 0x23000001 (AssemblyRef)  0          
2: 0x01000001 (TypeRef)      0          
3: 0x06000001 (MethodDef)    0          

EnC Map (0x1f):
=====================================================
   Entity                    Gen  Row       Edit    
=====================================================
1: 0x23000001 (AssemblyRef)  0    0x000001  update  
2: 0x01000001 (TypeRef)      0    0x000001  update  
3: 0x06000001 (MethodDef)    0    0x000001  update  

AssemblyRef (0x23):
================================================================================================================
   Name                         Version   Culture  PublicKeyOrToken                   Flags                   
================================================================================================================
1: 'System.Runtime' (#258/1:1)  10.0.0.0  nil      B0-3F-5F-7F-11-D5-0A-3A (#59/1:1)  0x00000001 (PublicKey)  

#US (size = 4):
  0: ''
  1: ''
  2: ''
  3: ''

#String (size = 45):
  0: ''
  1: 'System.Runtime'
  10: 'GetValue'
  19: '0.dll'
  1f: 'System'
  26: 'Object'

#Blob (size = 16):
  0: <empty>
  1 (Key): B0-3F-5F-7F-11-D5-0A-3A
  a (Key): 00-00-08
  e: <empty>
  f: <empty>

Sizes:
  Key: 11 bytes
#Guid (size = 48):
  1: {00000000-0000-0000-0000-000000000000}
  2: {aeecabc8-5a3b-2ff1-b6c1-164e5acc0694}
  3: {4c527cf5-a25e-41c0-b73c-748af0100c21}

Method 'TestApp.SimpleLib.GetValue' (0x06000001)
{
  // Code size        3 (0x3)
  .maxstack  8
  IL_0000:  ldc.i4.s   43
  IL_0002:  ret
}
```

Some tools you should have installed:

- https://github.com/dotnet/metadata-tools
- .NET 10 (I actually don't think you need this, I had it working with 9 at some point but when I most recently tried to switch back to 9 from 10 something broke.  So until I fix that, just use .NET 10)
- The `csharp_delta_test` folder has to do with using https://github.com/dotnet/hotreload-utils/.  This is what required me to use .NET 10 in the first place.  To get it working I had to clone it locally and build it.  What it will do is generate a dll as well as the 1.il, 1.meta, and 1.pdb files that comprise the patch.


I suppose if you have questions, feel free to reach me on the F# Discord or open an issue.  I'll do my best to respond.