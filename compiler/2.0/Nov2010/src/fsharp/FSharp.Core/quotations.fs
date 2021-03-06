﻿//----------------------------------------------------------------------------
//
// Copyright (c) 2002-2010 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------

namespace Microsoft.FSharp.Quotations

#if FX_MINIMAL_REFLECTION
#else
open System
open System.IO
open System.Reflection
open System.Collections.Generic
open Microsoft.FSharp
open Microsoft.FSharp.Core
open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators
open Microsoft.FSharp.Core.Operators
open Microsoft.FSharp.Primitives.Basics
open Microsoft.FSharp.Collections
open Microsoft.FSharp.Reflection
open Microsoft.FSharp.Core.Printf
open Microsoft.FSharp.Text.StructuredPrintfImpl
open Microsoft.FSharp.Text.StructuredPrintfImpl.LayoutOps

#nowarn "52" //  The value has been copied to ensure the original is not mutated by this operation

//--------------------------------------------------------------------------
// RAW quotations - basic data types
//--------------------------------------------------------------------------

module Helpers = 
    let qOneOrMoreRLinear q inp =
        let rec queryAcc rvs e = 
            match q e with 
            | Some(v,body) -> queryAcc (v::rvs) body 
            | None -> 
                match rvs with 
                | [] -> None
                | _ -> Some(List.rev rvs,e) 
        queryAcc [] inp 

    let qOneOrMoreLLinear q inp =
        let rec queryAcc e rvs = 
            match q e with 
            | Some(body,v) -> queryAcc body (v::rvs) 
            | None -> 
                match rvs with 
                | [] -> None
                | _ -> Some(e,rvs) 
        queryAcc inp []

    let mkRLinear mk (vs,body) = List.foldBack (fun v acc -> mk(v,acc)) vs body 
    let mkLLinear mk (body,vs) = List.fold (fun acc v -> mk(acc,v)) body vs 

    let staticBindingFlags = BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
    let staticOrInstanceBindingFlags = BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
    let instanceBindingFlags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
    let publicOrPrivateBindingFlags = System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic

    let isDelegateType (typ:Type) = 
        if typ.IsSubclassOf(typeof<Delegate>) then
            match typ.GetMethod("Invoke", instanceBindingFlags) with
            | null -> false
            | _ -> true
        else
            false

    let getDelegateInvoke ty = 
        if not (isDelegateType(ty)) then invalidArg  "ty" (SR.GetString(SR.delegateExpected))
        ty.GetMethod("Invoke", instanceBindingFlags)


    let inline checkNonNull argName (v: 'T) = 
        match box v with 
        | null -> nullArg argName 
        | _ -> ()
        
open Helpers


[<Sealed>]
[<CompiledName("FSharpVar")>]
[<System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage","CA2218:OverrideGetHashCodeOnOverridingEquals",Justification="Equals override does not equate further objects, so default GetHashCode is still valid")>]
type Var(name: string, typ:Type, ?isMutable: bool) =

    inherit obj()
    static let mutable lastStamp = 0L
    static let globals = new Dictionary<(string*Type),Var>(11)

    let stamp = lastStamp    
    let isMutable = defaultArg isMutable false
    do lock globals (fun () -> lastStamp <- lastStamp + 1L)
    
    member v.Name = name
    member v.IsMutable = isMutable
    member v.Type = typ
    member v.Stamp = stamp
    
    static member Global(name,typ: Type) = 
        checkNonNull "name" name
        checkNonNull "typ" typ
        lock globals (fun () -> 
            let mutable res = Unchecked.defaultof<Var>
            let ok = globals.TryGetValue((name,typ),&res)
            if ok then res else
            let res = new Var(name,typ)
            globals.[(name,typ)] <- res
            res)

    override v.ToString() = name

    override v.GetHashCode() = base.GetHashCode()

    override v.Equals(obj:obj) = 
        match obj with 
        | :? Var as v2 -> System.Object.ReferenceEquals(v,v2)
        | _ -> false

    interface System.IComparable with 
        member v.CompareTo(obj:obj) = 
            match obj with 
            | :? Var as v2 -> 
                if System.Object.ReferenceEquals(v,v2) then 0 else
                let c = compare v.Name v2.Name 
                if c <> 0 then c else 
#if FX_NO_REFLECTION_METADATA_TOKENS // not available on Compact Framework
#else
                let c = compare v.Type.MetadataToken v2.Type.MetadataToken in 
                if c <> 0 then c else 
                let c = compare v.Type.Module.MetadataToken v2.Type.Module.MetadataToken in 
                if c <> 0 then c else 
#endif
                let c = compare v.Type.Assembly.FullName v2.Type.Assembly.FullName in 
                if c <> 0 then c else 
                compare v.Stamp v2.Stamp
            | _ -> 0

/// Represents specifications of a subset of F# expressions 
[<StructuralEquality; NoComparison>]
type Tree =
    | CombTerm   of ExprConstInfo * Expr list
    | VarTerm    of Var
    | LambdaTerm of Var * Expr 
    | HoleTerm   of Type * int

and 
  [<StructuralEquality; NoComparison>]
  ExprConstInfo = 
    | AppOp
    | IfThenElseOp  
    | LetRecOp  
    | LetRecCombOp  
    | LetOp  
    | NewRecordOp      of Type
    | NewUnionCaseOp       of UnionCaseInfo
    | UnionCaseTestOp  of UnionCaseInfo
    | NewTupleOp     of Type
    | TupleGetOp    of Type * int
    | InstancePropGetOp    of PropertyInfo
    | StaticPropGetOp    of PropertyInfo
    | InstancePropSetOp    of PropertyInfo
    | StaticPropSetOp    of PropertyInfo
    | InstanceFieldGetOp   of FieldInfo
    | StaticFieldGetOp   of FieldInfo
    | InstanceFieldSetOp   of FieldInfo
    | StaticFieldSetOp   of FieldInfo
    | NewObjectOp   of ConstructorInfo 
    | InstanceMethodCallOp of MethodInfo 
    | StaticMethodCallOp of MethodInfo 
    | CoerceOp     of Type
    | NewArrayOp    of Type
    | NewDelegateOp   of Type
    | QuoteOp 
    | SequentialOp 
    | AddressOfOp 
    | VarSetOp
    | AddressSetOp 
    | TypeTestOp  of Type
    | TryWithOp 
    | TryFinallyOp 
    | ForIntegerRangeLoopOp 
    | WhileLoopOp 
    // Arbitrary spliced values - not serialized
    | ValueOp of obj * Type
    | DefaultValueOp of Type
    
and [<CompiledName("FSharpExpr")>]
    Expr(term:Tree,attribs:Expr list) =
    member x.Tree = term
    member x.CustomAttributes = attribs 
    override x.Equals(obj:obj) =
        match obj with 
        | :? Expr as yt -> x.Tree = yt.Tree
        | _ -> false

    override x.GetHashCode() = 
        x.Tree.GetHashCode() 

    override x.ToString() = 
        Microsoft.FSharp.Text.StructuredPrintfImpl.Display.layout_to_string Microsoft.FSharp.Text.StructuredPrintfImpl.FormatOptions.Default (x.GetLayout())
        
    member x.GetLayout() = 
        let expr (e:Expr ) = e.GetLayout()
        let exprs (es:Expr list) = es |> List.map expr
        let parens ls = bracketL (commaListL ls)
        let pairL l1 l2 = bracketL (l1 ^^ sepL "," ^^ l2)
        let listL ls = squareBracketL (commaListL ls)
        let combL nm ls = wordL nm ^^ parens ls
        let noneL = wordL "None"
        let someL e = combL "Some" [expr e]
        let typeL (o: Type)  = wordL o.FullName
        let objL (o: 'T)  = wordL (sprintf "%A" o)
        let varL (v:Var) = wordL v.Name
        let (|E|) (e: Expr) = e.Tree
        let (|Lambda|_|)        (E x) = match x with LambdaTerm(a,b)  -> Some (a,b) | _ -> None 
        let (|IteratedLambda|_|) (e: Expr) = qOneOrMoreRLinear (|Lambda|_|) e
        
        let rec (|NLambdas|_|) n (e:Expr) = 
            match e with 
            | _ when n <= 0 -> Some([],e) 
            | Lambda(v,NLambdas ((-) n 1) (vs,b)) -> Some(v::vs,b)
            | _ -> None

        match x.Tree with 
        | CombTerm(AppOp,args)                     -> combL "Application" (exprs args)
        | CombTerm(IfThenElseOp,args)              -> combL "IfThenElse" (exprs args)
        | CombTerm(LetRecOp,[IteratedLambda(vs,E(CombTerm(LetRecCombOp,b2::bs)))]) -> combL "LetRecursive" [listL (List.map2 pairL (List.map varL vs) (exprs bs) ); b2.GetLayout()]
        | CombTerm(LetOp,[e;E(LambdaTerm(v,b))]) -> combL "Let" [varL v; e.GetLayout(); b.GetLayout()]
        | CombTerm(NewRecordOp(ty),args)           -> combL "NewRecord" (typeL ty :: exprs args)
        | CombTerm(NewUnionCaseOp(unionCase),args)    -> combL "NewUnionCase" (objL unionCase :: exprs args)
        | CombTerm(UnionCaseTestOp(unionCase),args)   -> combL "UnionCaseTest" (exprs args@ [objL unionCase])
        | CombTerm(NewTupleOp _,args)            -> combL "NewTuple" (exprs args)
        | CombTerm(TupleGetOp (_,i),[arg])         -> combL "TupleGet" ([expr arg] @ [objL i])
        | CombTerm(ValueOp(v,_),[])               -> combL "Value" [objL v]
        | CombTerm(InstanceMethodCallOp(minfo),obj::args) -> combL "Call"     [someL obj; objL minfo; listL (exprs args)]
        | CombTerm(StaticMethodCallOp(minfo),args)        -> combL "Call"     [noneL;     objL minfo; listL (exprs args)]
        | CombTerm(InstancePropGetOp(pinfo),(obj::args))  -> combL "PropertyGet"  [someL obj; objL pinfo; listL (exprs args)]
        | CombTerm(StaticPropGetOp(pinfo),args)           -> combL "PropertyGet"  [noneL;     objL pinfo; listL (exprs args)]
        | CombTerm(InstancePropSetOp(pinfo),(obj::args))  -> combL "PropertySet"  [someL obj; objL pinfo; listL (exprs args)]
        | CombTerm(StaticPropSetOp(pinfo),args)           -> combL "PropertySet"  [noneL;     objL pinfo; listL (exprs args)]
        | CombTerm(InstanceFieldGetOp(finfo),[obj])       -> combL "FieldGet" [someL obj; objL finfo]
        | CombTerm(StaticFieldGetOp(finfo),[])            -> combL "FieldGet" [noneL;     objL finfo]
        | CombTerm(InstanceFieldSetOp(finfo),[obj;v])       -> combL "FieldSet" [someL obj; objL finfo; expr v;]
        | CombTerm(StaticFieldSetOp(finfo),[v])            -> combL "FieldSet" [noneL;     objL finfo; expr v;]
        | CombTerm(CoerceOp(ty),[arg])                    -> combL "Coerce"  [ expr arg; typeL ty]
        | CombTerm(NewObjectOp minfo,args)   -> combL "NewObject" ([ objL minfo ] @ exprs args)
        | CombTerm(DefaultValueOp(ty),args)  -> combL "DefaultValue" ([ typeL ty ] @ exprs args)
        | CombTerm(NewArrayOp(ty),args)      -> combL "NewArray" ([ typeL ty ] @ exprs args)
        | CombTerm(TypeTestOp(ty),args)      -> combL "TypeTest" ([ typeL ty] @ exprs args)
        | CombTerm(AddressOfOp,args)         -> combL "AddressOf" (exprs args)
        | CombTerm(VarSetOp,[E(VarTerm(v)); e])  -> combL "VarSet" [varL v; expr e]
        | CombTerm(AddressSetOp,args)        -> combL "AddressSet" (exprs args)
        | CombTerm(ForIntegerRangeLoopOp,[e1;e2;E(LambdaTerm(v,e3))])     -> combL "ForIntegerRangeLoop" [varL v; expr e1; expr e2; expr e3]
        | CombTerm(WhileLoopOp,args)         -> combL "WhileLoop" (exprs args)
        | CombTerm(TryFinallyOp,args)         -> combL "TryFinally" (exprs args)
        | CombTerm(TryWithOp,[e1;Lambda(v1,e2);Lambda(v2,e3)])         -> combL "TryWith" [expr e1; varL v1; expr e2; varL v2; expr e3]
        | CombTerm(SequentialOp,args)        -> combL "Sequential" (exprs args)
        | CombTerm(NewDelegateOp(ty),[e])   -> 
            let n = (getDelegateInvoke ty).GetParameters().Length
            match e with 
            | NLambdas n (vs,e) -> combL "NewDelegate" ([typeL ty] @ (vs |> List.map varL) @ [expr e])
            | _ -> combL "NewDelegate" ([typeL ty; expr e])
        //| CombTerm(_,args)   -> combL "??" (exprs args)
        | VarTerm(v)   -> wordL v.Name
        | LambdaTerm(v,b)   -> combL "Lambda" [wordL v.Name; expr b]
        | HoleTerm _  -> wordL "_"
        | CombTerm(QuoteOp,args) -> combL "Quote" (exprs args)
        | _ -> failwithf "Unexpected term in layout %A" x.Tree

     

and [<CompiledName("FSharpExpr`1")>]
    Expr<'T>(term:Tree,attribs) = 
    inherit Expr(term,attribs)
    member x.Raw = (x :> Expr)

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Patterns = 

    /// Internal type representing a deserialized object that is yet to be instantiated. Representation is
    /// as a computation.
    type Instantiable<'T> = (int -> Type) -> 'T

    type ByteStream(bytes:byte[], initial:int, len:int) = 
    
        let mutable pos = initial
        let lim = initial + len
        
        member b.ReadByte() = 
            if pos >= lim then failwith "end of stream";
            let res = int32 bytes.[pos]
            pos <- pos + 1;
            res 
        
        member b.ReadBytes n  = 
            if pos + n > lim then failwith "ByteStream.ReadBytes: end of stream";
            let res = bytes.[pos..pos+n-1]
            pos <- pos + n;
            res 

        member b.ReadUtf8BytesAsString n = 
            let res = System.Text.Encoding.UTF8.GetString(bytes,pos,n)
            pos <- pos + n;
            res


    let E t = new Expr< >(t,[])
    let EA (t,attribs) = new Expr< >(t,attribs)
    let ES ts = List.map E ts

    let (|E|) (e: Expr) = e.Tree
    let (|ES|) (es: list<Expr>) = es |> List.map (fun e -> e.Tree)
    let (|FrontAndBack|_|) es = 
        let rec loop acc xs = match xs with [] -> None | [h] -> Some (List.rev acc, h) | h::t -> loop (h::acc) t
        loop [] es



    let funTyC = typeof<(obj -> obj)>.GetGenericTypeDefinition()  
    let exprTyC = typedefof<Expr<int>>
    let voidTy = typeof<System.Void>
    let unitTy = typeof<unit>
    let removeVoid a = if a = voidTy then unitTy else a
    let addVoid a = if a = unitTy then voidTy else a
    let mkFunTy a b = 
        let (a, b) = removeVoid a, removeVoid b
        funTyC.MakeGenericType([| a;b |])

    let mkArrayTy (t:Type) = t.MakeArrayType();
    let mkExprTy (t:Type) = exprTyC.MakeGenericType([| t |])


    //--------------------------------------------------------------------------
    // Active patterns for decomposing quotations
    //--------------------------------------------------------------------------

    let (|Comb0|_|) (E x) = match x with CombTerm(k,[])  -> Some(k) | _ -> None

    let (|Comb1|_|) (E x) = match x with CombTerm(k,[x]) -> Some(k,x) | _ -> None

    let (|Comb2|_|) (E x) = match x with CombTerm(k,[x1;x2]) -> Some(k,x1,x2) | _ -> None

    let (|Comb3|_|) (E x) = match x with CombTerm(k,[x1;x2;x3]) -> Some(k,x1,x2,x3) | _ -> None
    
    [<CompiledName("VarPattern")>]
    let (|Var|_|)           (E x) = match x with VarTerm v        -> Some v     | _ -> None 

    [<CompiledName("ApplicationPattern")>]
    let (|Application|_|)      x = match x with Comb2(AppOp,a,b) -> Some (a,b) | _ -> None 

    [<CompiledName("LambdaPattern")>]
    let (|Lambda|_|)        (E x) = match x with LambdaTerm(a,b)  -> Some (a,b) | _ -> None 

    [<CompiledName("QuotePattern")>]
    let (|Quote|_|)         (E x) = match x with CombTerm(QuoteOp,[a])     -> Some (a)   | _ -> None 

    [<CompiledName("IfThenElsePattern")>]
    let (|IfThenElse|_|)          = function Comb3(IfThenElseOp,e1,e2,e3) -> Some(e1,e2,e3) | _ -> None

    [<CompiledName("NewTuplePattern")>]
    let (|NewTuple|_|)         = function E(CombTerm(NewTupleOp(_),es)) -> Some(es) | _ -> None

    [<CompiledName("DefaultValuePattern")>]
    let (|DefaultValue|_|)         = function E(CombTerm(DefaultValueOp(ty),[])) -> Some(ty) | _ -> None

    [<CompiledName("NewRecordPattern")>]
    let (|NewRecord|_|)          = function E(CombTerm(NewRecordOp(x),es)) -> Some(x,es) | _ -> None

    [<CompiledName("NewUnionCasePattern")>]
    let (|NewUnionCase|_|)           = function E(CombTerm(NewUnionCaseOp(unionCase),es)) -> Some(unionCase,es) | _ -> None

    [<CompiledName("UnionCaseTestPattern")>]
    let (|UnionCaseTest|_|)    = function Comb1(UnionCaseTestOp(unionCase),e) -> Some(e,unionCase) | _ -> None

    [<CompiledName("TupleGetPattern")>]
    let (|TupleGet|_|)      = function Comb1(TupleGetOp(_,n),e) -> Some(e,n) | _ -> None

    [<CompiledName("CoercePattern")>]
    let (|Coerce|_|)        = function Comb1(CoerceOp ty,e1) -> Some(e1,ty) | _ -> None

    [<CompiledName("TypeTestPattern")>]
    let (|TypeTest|_|)        = function Comb1(TypeTestOp ty,e1) -> Some(e1,ty) | _ -> None

    [<CompiledName("NewArrayPattern")>]
    let (|NewArray|_|)      = function E(CombTerm(NewArrayOp ty,es)) -> Some(ty,es) | _ -> None

    [<CompiledName("AddressSetPattern")>]
    let (|AddressSet|_|)    = function E(CombTerm(AddressSetOp,[e;v])) -> Some(e,v) | _ -> None

    [<CompiledName("TryFinallyPattern")>]
    let (|TryFinally|_|)    = function E(CombTerm(TryFinallyOp,[e1;e2])) -> Some(e1,e2) | _ -> None

    [<CompiledName("TryWithPattern")>]
    let (|TryWith|_|)      = function E(CombTerm(TryWithOp,[e1;Lambda(v1,e2);Lambda(v2,e3)])) -> Some(e1,v1,e2,v2,e3) | _ -> None

    [<CompiledName("VarSetPattern")>]
    let (|VarSet|_|    )    = function E(CombTerm(VarSetOp,[E(VarTerm(v)); e])) -> Some(v,e) | _ -> None

    [<CompiledName("ValuePattern")>]
    let (|Value|_|)         = function E(CombTerm(ValueOp (v,ty),_)) -> Some(v,ty) | _ -> None

    [<CompiledName("ValueObjPattern")>]
    let (|ValueObj|_|)      = function E(CombTerm(ValueOp (v,_),_)) -> Some(v) | _ -> None

    [<CompiledName("AddressOfPattern")>]
    let (|AddressOf|_|)       = function Comb1(AddressOfOp,e) -> Some(e) | _ -> None

    [<CompiledName("SequentialPattern")>]
    let (|Sequential|_|)    = function Comb2(SequentialOp,e1,e2) -> Some(e1,e2) | _ -> None

    [<CompiledName("ForIntegerRangeLoopPattern")>]
    let (|ForIntegerRangeLoop|_|)       = function Comb3(ForIntegerRangeLoopOp,e1,e2,Lambda(v, e3)) -> Some(v,e1,e2,e3) | _ -> None

    [<CompiledName("WhileLoopPattern")>]
    let (|WhileLoop|_|)     = function Comb2(WhileLoopOp,e1,e2) -> Some(e1,e2) | _ -> None

    [<CompiledName("PropertyGetPattern")>]
    let (|PropertyGet|_|)       = function E(CombTerm(StaticPropGetOp pinfo,args)) -> Some(None,pinfo,args) 
                                         | E(CombTerm(InstancePropGetOp pinfo,obj::args)) -> Some(Some(obj),pinfo,args) 
                                         | _ -> None

    [<CompiledName("PropertySetPattern")>]
    let (|PropertySet|_|)   x    = 
        match x with 
        | E(CombTerm(StaticPropSetOp pinfo, FrontAndBack(args,v))) -> Some(None,pinfo,args,v) 
        | E(CombTerm(InstancePropSetOp pinfo, obj::FrontAndBack(args,v))) -> Some(Some(obj),pinfo,args,v) 
        | _ -> None


    [<CompiledName("FieldGetPattern")>]
    let (|FieldGet|_|)      = function E(CombTerm(StaticFieldGetOp finfo,[])) -> Some(None,finfo) 
                                     | E(CombTerm(InstanceFieldGetOp finfo,[obj])) -> Some(Some(obj),finfo) 
                                     | _ -> None
    [<CompiledName("FieldSetPattern")>]
    let (|FieldSet|_|)      = function E(CombTerm(StaticFieldSetOp finfo,[v])) -> Some(None,finfo,v) 
                                     | E(CombTerm(InstanceFieldSetOp finfo,[obj;v])) -> Some(Some(obj),finfo,v) 
                                     | _ -> None
    [<CompiledName("NewObjectPattern")>]
    let (|NewObject|_|)      = function E(CombTerm(NewObjectOp ty,e)) -> Some(ty,e) | _ -> None

    [<CompiledName("CallPattern")>]
    let (|Call|_|)           = function E(CombTerm(StaticMethodCallOp minfo,args)) -> Some(None,minfo,args) 
                                      | E(CombTerm(InstanceMethodCallOp minfo,(obj::args))) -> Some(Some(obj),minfo,args) 
                                      | _ -> None
    let (|LetRaw|_|)        = function Comb2(LetOp,e1,e2) -> Some(e1,e2) | _ -> None

    let (|LetRecRaw|_|)     = function Comb1(LetRecOp,e1) -> Some(e1) | _ -> None

    [<CompiledName("LetPattern")>]
    let (|Let|_|) = function LetRaw(e,Lambda(v,body)) -> Some(v,e,body) | _ -> None

    let (|IteratedLambda|_|) (e: Expr) = qOneOrMoreRLinear (|Lambda|_|) e 

    let rec (|NLambdas|_|) n (e:Expr) = 
        match e with 
        | _ when n <= 0 -> Some([],e) 
        | Lambda(v,NLambdas ((-) n 1) (vs,b)) -> Some(v::vs,b)
        | _ -> None

    [<CompiledName("NewDelegatePattern")>]
    let (|NewDelegate|_|) e  = 
        match e with 
        | Comb1(NewDelegateOp(ty),e) -> 
            let n = (getDelegateInvoke ty).GetParameters().Length
            match e with 
            | NLambdas n (vs,e) -> Some(ty,vs,e) 
            | _ -> None
        | _ -> None

    [<CompiledName("LetRecursivePattern")>]
    let (|LetRecursive|_|) e = 
        match e with 
        | LetRecRaw(IteratedLambda(vs1,E(CombTerm(LetRecCombOp,body::es)))) -> Some(List.zip vs1 es,body)
        | _ -> None
    
    //--------------------------------------------------------------------------
    // Getting the type of Raw quotations
    //--------------------------------------------------------------------------

    // Returns record member specified by name
    let getRecordProperty(ty,fieldName) =    
        let mems = FSharpType.GetRecordFields(ty,publicOrPrivateBindingFlags)
        match mems |> Array.tryFind (fun minfo -> minfo.Name = fieldName) with
        | Some (m) -> m
        | _ -> invalidArg  "fieldName" (SR.GetString2(SR.QmissingRecordField, ty.FullName, fieldName))

    let getUnionCaseInfo(ty,unionCaseName) =    
        let cases = FSharpType.GetUnionCases(ty,publicOrPrivateBindingFlags)
        match cases |> Array.tryFind (fun ucase -> ucase.Name = unionCaseName) with
        | Some(case) -> case
        | _ -> invalidArg  "unionCaseName" (SR.GetString2(SR.QmissingUnionCase, ty.FullName, unionCaseName))
    
    let getUnionCaseInfoField(unionCase:UnionCaseInfo,index) =    
        let fields = unionCase.GetFields() 
        if index < 0 || index >= fields.Length then invalidArg "index" (SR.GetString(SR.QinvalidCaseIndex))
        fields.[index]
 
    /// Returns type of lambda applciation - something like "(fun a -> ..) b"
    let rec typeOfAppliedLambda f =
        let fty = ((typeOf f):Type) 
        match fty.GetGenericArguments() with 
        | [| _; b|] -> b
        | _ -> raise <| System.InvalidOperationException (SR.GetString(SR.QillFormedAppOrLet))          

    /// Returns type of the Raw quotation or fails if the quotation is ill formed
    /// if 'verify' is true, verifies all branches, otherwise ignores some of them when not needed
    and typeOf<'T when 'T :> Expr> (e : 'T) : Type = 
        let (E t) = e 
        match t with 
        | VarTerm    v        -> v.Type
        | LambdaTerm (v,b)    -> mkFunTy v.Type (typeOf b)
        | HoleTerm   (ty,_)   -> ty
        | CombTerm   (c,args) -> 
            match c,args with 
            | AppOp,[f;_] -> typeOfAppliedLambda f
            | LetOp,_ -> match e with Let(_,_,b) -> typeOf b | _ -> failwith "unreachable"
            | IfThenElseOp,[_;t;_]  -> typeOf t
            | LetRecOp,_     -> match e with LetRecursive(_,b) -> typeOf b | _ -> failwith "unreachable"
            | LetRecCombOp,_        -> failwith "typeOfConst: LetRecCombOp" 
            | NewRecordOp ty,_         -> ty
            | NewUnionCaseOp unionCase,_   -> unionCase.DeclaringType
            | UnionCaseTestOp _,_ -> typeof<Boolean>
            | ValueOp (_, ty),_  -> ty
            | TupleGetOp (ty,i),_ -> FSharpType.GetTupleElements(ty).[i] 
            | NewTupleOp ty,_      -> ty
            | StaticPropGetOp prop,_    -> prop.PropertyType
            | InstancePropGetOp prop,_    -> prop.PropertyType
            | StaticPropSetOp _,_   -> typeof<Unit>
            | InstancePropSetOp _,_    -> typeof<Unit>
            | InstanceFieldGetOp fld ,_   -> fld.FieldType
            | StaticFieldGetOp fld ,_   -> fld.FieldType
            | InstanceFieldSetOp _,_    -> typeof<Unit>
            | StaticFieldSetOp _,_    -> typeof<Unit>
            | NewObjectOp ctor,_   -> ctor.DeclaringType
            | InstanceMethodCallOp minfo,_   -> minfo.ReturnType |> removeVoid
            | StaticMethodCallOp minfo,_   -> minfo.ReturnType |> removeVoid
            | CoerceOp ty,_       -> ty
            | SequentialOp,[_;b]      -> typeOf b 
            | ForIntegerRangeLoopOp,_  -> typeof<Unit>
            | NewArrayOp ty,_      -> mkArrayTy ty
            | NewDelegateOp ty,_     -> ty
            | DefaultValueOp ty,_     -> ty
            | TypeTestOp _,_     -> typeof<bool>
            | QuoteOp,[expr]        -> mkExprTy (typeOf expr)
            | TryFinallyOp,[e1;_]        -> typeOf e1
            | TryWithOp,[e1;_;_]        -> typeOf e1
            | WhileLoopOp,_ 
            | VarSetOp,_
            | AddressSetOp,_ -> typeof<Unit> 
            | AddressOfOp,_ -> raise <| System.InvalidOperationException (SR.GetString(SR.QcannotTakeAddress))
            | (QuoteOp | SequentialOp | TryWithOp | TryFinallyOp | IfThenElseOp | AppOp),_ -> failwith "unreachable"


    //--------------------------------------------------------------------------
    // Constructors for building Raw quotations
    //--------------------------------------------------------------------------
      
    let mkFEN op l = E(CombTerm(op,l))
    let mkFE0 op = E(CombTerm(op,[]))
    let mkFE1 op x = E(CombTerm(op,[(x:>Expr)]))
    let mkFE2 op (x,y) = E(CombTerm(op,[(x:>Expr);(y:>Expr)]))
    let mkFE3 op (x,y,z) = E(CombTerm(op,[(x:>Expr);(y:>Expr);(z:>Expr)])  )
    let mkOp v () = v

    let mkLetRaw v = mkFE2 LetOp v

    //--------------------------------------------------------------------------
    // Type-checked constructors for building Raw quotations
    //--------------------------------------------------------------------------
  
    // t2 is inherited from t1 / t2 implements interface t1 or t2 == t1
    let assignableFrom (t1:Type) (t2:Type) =  
        t1.IsAssignableFrom(t2)
      
    let checkTypesSR (expectedType: Type) (receivedType : Type)  name threeHoleSR =
        if (expectedType <> receivedType) then 
          invalidArg "receivedType" (String.Format(threeHoleSR, name, expectedType, receivedType))

    let checkTypesWeakSR (expectedType: Type) (receivedType : Type)  name threeHoleSR = 
        if (not (assignableFrom expectedType receivedType)) then 
          invalidArg "receivedType" (String.Format(threeHoleSR, name, expectedType, receivedType))
  
    let checkArgs (paramInfos: ParameterInfo[]) (args:list<Expr>) =  
        if (paramInfos.Length <> args.Length) then invalidArg "args" (SR.GetString(SR.QincorrectNumArgs))
        List.iter2
            ( fun (p:ParameterInfo) a -> checkTypesWeakSR p.ParameterType (typeOf a) "args" (SR.GetString(SR.QtmmInvalidParam))) 
            (paramInfos |> Array.toList) 
            args
                                                // todo: shouldn't this be "strong" type check? sometimes?

    let checkAssignableFrom ty1 ty2 = 
        if not (assignableFrom ty1 ty2) then invalidArg "ty2" (SR.GetString(SR.QincorrectType))

    let checkObj  (membInfo: MemberInfo) (obj: Expr) = 
        // The MemberInfo may be a property associated with a union
        // find the actual related union type
        let rec loop (ty:Type) = if FSharpType.IsUnion ty && FSharpType.IsUnion ty.BaseType then loop ty.BaseType else ty
        let declType = loop membInfo.DeclaringType
        if not (assignableFrom declType (typeOf obj)) then invalidArg "obj" (SR.GetString(SR.QincorrectInstanceType))

      
    // Checks lambda application for correctnes
    let checkAppliedLambda (f, v) =
        let fty = typeOf f
        let ftyG = (if fty.IsGenericType then  fty.GetGenericTypeDefinition()  else fty)
        checkTypesSR funTyC ftyG "f" (SR.GetString(SR.QtmmExpectedFunction))
        let vty = (typeOf v)
        match fty.GetGenericArguments() with 
          | [| a; _ |] -> checkTypesSR vty a "f" (SR.GetString(SR.QtmmFunctionArgTypeMismatch))
          | _ -> invalidArg  "f" (SR.GetString(SR.QinvalidFuncType))
  
    // Returns option (by name) of a NewUnionCase type
    let getUnionCaseFields ty str =       
        let cases = FSharpType.GetUnionCases(ty,publicOrPrivateBindingFlags)
        match cases |> Array.tryFind (fun ucase -> ucase.Name = str) with
        | Some(case) -> case.GetFields()
        | _ -> invalidArg  "ty" (SR.GetString1(SR.notAUnionType, ty.FullName))
  
    let checkBind(v:Var,e) = 
        let ety = typeOf e
        checkTypesSR v.Type ety "let" (SR.GetString(SR.QtmmVarTypeNotMatchRHS))
  
    // [Correct by definition]
    let mkVar v       = E(VarTerm v )
    let mkQuote(a)    = E(CombTerm(QuoteOp,[(a:>Expr)] ))
          
    let mkValue (v,ty) = mkFE0 (ValueOp(v,ty))
    let mkValueG (v:'T) = mkValue(box v, typeof<'T>)
    let mkLiftedValueOpG (v, ty: System.Type) = 
        let obj = if ty.IsEnum then System.Enum.ToObject(ty, box v) else box v
        ValueOp(obj, ty)
    let mkUnit       () = mkValue(null, typeof<unit>)
    let mkAddressOf     v = mkFE1 AddressOfOp v
    let mkSequential  (e1,e2) = mkFE2 SequentialOp (e1,e2) 
    let mkTypeTest    (e,ty) = mkFE1 (TypeTestOp(ty)) e
    let mkVarSet    (v,e) = mkFE2 VarSetOp (mkVar(v),e)
    let mkAddressSet    (e1,e2) = mkFE2 AddressSetOp (e1,e2)
    let mkLambda(var,body) = E(LambdaTerm(var,(body:>Expr)))
    let mkTryWith(e1,v1,e2,v2,e3) = mkFE3 TryWithOp (e1,mkLambda(v1,e2),mkLambda(v2,e3))
    let mkTryFinally(e1,e2) = mkFE2 TryFinallyOp (e1,e2)
    
    let mkCoerce      (ty,x) = mkFE1 (CoerceOp ty) x
    let mkNull        (ty)   = mkFE0 (ValueOp(null,ty))
    
    let mkApplication v = checkAppliedLambda v; mkFE2 AppOp v 

    // Tuples
    let mkNewTupleWithType    (ty,args:Expr list) = 
        let mems = FSharpType.GetTupleElements ty |> Array.toList
        if (args.Length <> mems.Length) then invalidArg  "args" (SR.GetString(SR.QtupleLengthsDiffer))
        List.iter2(fun mt a -> checkTypesSR mt (typeOf a) "args" (SR.GetString(SR.QtmmTuple)) ) mems args
        mkFEN (NewTupleOp ty) args 
    
    let mkNewTuple (args) = 
        let ty = FSharpType.MakeTupleType(Array.map typeOf (Array.ofList args))
        mkFEN (NewTupleOp ty) args
    
    let mkTupleGet (ty,n,x) = 
        checkTypesSR ty (typeOf x) "tupleGet" (SR.GetString(SR.QtmmExprNotMatchTuple))
        let mems = FSharpType.GetTupleElements ty 
        if (n < 0 || mems.Length <= n) then invalidArg  "n" (SR.GetString(SR.QtupleAccessOutOfRange))
        mkFE1 (TupleGetOp (ty,n)) x
    
    // Records
    let mkNewRecord (ty,args:list<Expr>) = 
        let mems = FSharpType.GetRecordFields(ty,publicOrPrivateBindingFlags) 
        if (args.Length <> mems.Length) then invalidArg  "args" (SR.GetString(SR.QincompatibleRecordLength))
        List.iter2 (fun (minfo:PropertyInfo) a -> checkTypesSR minfo.PropertyType (typeOf a) "recd" (SR.GetString(SR.QtmmIncorrectArgForRecord))) (Array.toList mems) args
        mkFEN (NewRecordOp ty) args
      
      
    // Discriminated unions        
    let mkNewUnionCase (unionCase:UnionCaseInfo,args:list<Expr>) = 
        if Unchecked.defaultof<UnionCaseInfo> = unionCase then raise (new ArgumentNullException())
        let sargs = unionCase.GetFields()
        if (args.Length <> sargs.Length) then invalidArg  "args" (SR.GetString(SR.QunionNeedsDiffNumArgs))
        List.iter2 (fun (minfo:PropertyInfo) a  -> checkTypesSR minfo.PropertyType (typeOf a) "sum" (SR.GetString(SR.QtmmIncorrectArgForUnion))) (Array.toList sargs) args
        mkFEN (NewUnionCaseOp unionCase) args
        
    let mkUnionCaseTest (unionCase:UnionCaseInfo,expr) = 
        if Unchecked.defaultof<UnionCaseInfo> = unionCase then raise (new ArgumentNullException())
        checkTypesSR unionCase.DeclaringType (typeOf expr) "UnionCaseTagTest" (SR.GetString(SR.QtmmExprTypeMismatch))
        mkFE1 (UnionCaseTestOp unionCase) expr

    // Conditional etc..
    let mkIfThenElse (e,t,f) = 
        checkTypesSR (typeOf t) (typeOf f) "cond" (SR.GetString(SR.QtmmTrueAndFalseMustMatch))
        checkTypesSR (typeof<Boolean>) (typeOf e) "cond" (SR.GetString(SR.QtmmCondMustBeBool))
        mkFE3 IfThenElseOp (e,t,f)               
        
    let mkNewArray (ty,args) = 
        List.iter (fun a -> checkTypesSR ty (typeOf a) "newArray" (SR.GetString(SR.QtmmInitArray))) args
        mkFEN (NewArrayOp ty) args
        
    let mkInstanceFieldGet(obj,finfo:FieldInfo) =
        if Unchecked.defaultof<FieldInfo> = finfo then raise (new ArgumentNullException())
        match finfo.IsStatic with 
        | false -> 
            checkObj finfo obj
            mkFE1 (InstanceFieldGetOp finfo) obj
        | true -> invalidArg  "finfo" (SR.GetString(SR.QstaticWithReceiverObject))
      
    let mkStaticFieldGet    (finfo:FieldInfo) =
        if Unchecked.defaultof<FieldInfo> = finfo then raise (new ArgumentNullException())
        match finfo.IsStatic with 
        | true -> mkFE0 (StaticFieldGetOp finfo) 
        | false -> invalidArg  "finfo" (SR.GetString(SR.QnonStaticNoReceiverObject))
      
    let mkStaticFieldSet (finfo:FieldInfo,value:Expr) =
        if Unchecked.defaultof<FieldInfo> = finfo then raise (new ArgumentNullException())
        checkTypesSR (typeOf value) finfo.FieldType "value" (SR.GetString(SR.QtmmBadFieldType))
        match finfo.IsStatic with 
        | true -> mkFE1 (StaticFieldSetOp finfo) value
        | false -> invalidArg  "finfo" (SR.GetString(SR.QnonStaticNoReceiverObject))
      
    let mkInstanceFieldSet (obj,finfo:FieldInfo,value:Expr) =
        if Unchecked.defaultof<FieldInfo> = finfo then raise (new ArgumentNullException())
        checkTypesSR (typeOf value) finfo.FieldType "value" (SR.GetString(SR.QtmmBadFieldType))
        match finfo.IsStatic with 
        | false -> 
            checkObj finfo obj
            mkFE2 (InstanceFieldSetOp finfo) (obj,value)
        | true -> invalidArg  "finfo" (SR.GetString(SR.QstaticWithReceiverObject))
      
    let mkCtorCall (ci:ConstructorInfo,args:list<Expr>) =
        if Unchecked.defaultof<ConstructorInfo> = ci then raise (new ArgumentNullException())
        checkArgs (ci.GetParameters()) args
        mkFEN (NewObjectOp ci) args

    let mkDefaultValue (ty:Type) =
        mkFE0 (DefaultValueOp ty) 

    let mkStaticPropGet (pinfo:PropertyInfo,args:list<Expr>) = 
        if Unchecked.defaultof<PropertyInfo> = pinfo then raise (new ArgumentNullException())
        if (not pinfo.CanRead) then invalidArg  "pinfo" (SR.GetString(SR.QreadingSetOnly))
        checkArgs (pinfo.GetIndexParameters()) args
        match pinfo.GetGetMethod(true).IsStatic with 
        | true -> mkFEN (StaticPropGetOp  pinfo) args
        | false -> invalidArg  "pinfo" (SR.GetString(SR.QnonStaticNoReceiverObject))

    let mkInstancePropGet (obj,pinfo:PropertyInfo,args:list<Expr>) = 
        if Unchecked.defaultof<PropertyInfo> = pinfo then raise (new ArgumentNullException())
        if (not pinfo.CanRead) then invalidArg  "pinfo" (SR.GetString(SR.QreadingSetOnly))
        checkArgs (pinfo.GetIndexParameters()) args
        match pinfo.GetGetMethod(true).IsStatic with 
        | false -> 
            checkObj pinfo obj
            mkFEN (InstancePropGetOp pinfo) (obj::args)
        | true -> invalidArg  "pinfo" (SR.GetString(SR.QstaticWithReceiverObject))
          
    let mkStaticPropSet (pinfo:PropertyInfo,args:list<Expr>,value:Expr) = 
        if Unchecked.defaultof<PropertyInfo> = pinfo then raise (new ArgumentNullException())
        if (not pinfo.CanWrite) then invalidArg  "pinfo" (SR.GetString(SR.QwritingGetOnly))
        checkArgs (pinfo.GetIndexParameters()) args
        match pinfo.GetSetMethod(true).IsStatic with 
        | true -> mkFEN (StaticPropSetOp pinfo) (value::args)
        | false -> invalidArg  "pinfo" (SR.GetString(SR.QnonStaticNoReceiverObject))
          
    let mkInstancePropSet (obj,pinfo:PropertyInfo,args:list<Expr>,value:Expr) = 
        if Unchecked.defaultof<PropertyInfo> = pinfo then raise (new ArgumentNullException())
        if (not pinfo.CanWrite) then invalidArg  "pinfo" (SR.GetString(SR.QwritingGetOnly))
        checkArgs (pinfo.GetIndexParameters()) args
        match pinfo.GetSetMethod(true).IsStatic with 
        | false -> 
            checkObj pinfo obj
            mkFEN (InstancePropSetOp pinfo) (obj::value::args)
        | true -> invalidArg  "pinfo" (SR.GetString(SR.QstaticWithReceiverObject))
          
    let mkInstanceMethodCall (obj,minfo:MethodInfo,args:list<Expr>) =
        if Unchecked.defaultof<MethodInfo> = minfo then raise (new ArgumentNullException())
        checkArgs (minfo.GetParameters()) args
        match minfo.IsStatic with 
        | false -> 
            checkObj minfo obj
            mkFEN (InstanceMethodCallOp minfo) (obj::args)
        | true -> invalidArg  "minfo" (SR.GetString(SR.QstaticWithReceiverObject))
    
    let mkStaticMethodCall (minfo:MethodInfo,args:list<Expr>) =
        if Unchecked.defaultof<MethodInfo> = minfo then raise (new ArgumentNullException())
        checkArgs (minfo.GetParameters()) args
        match minfo.IsStatic with 
        | true -> mkFEN (StaticMethodCallOp minfo) args
        | false -> invalidArg  "minfo" (SR.GetString(SR.QnonStaticNoReceiverObject))
    
    let mkForLoop (v:Var,lowerBound,upperBound,body) = 
        checkTypesSR (typeof<int>) (typeOf lowerBound) "lowerBound" (SR.GetString(SR.QtmmLowerUpperBoundMustBeInt))
        checkTypesSR (typeof<int>) (typeOf upperBound) "upperBound" (SR.GetString(SR.QtmmLowerUpperBoundMustBeInt))
        checkTypesSR (typeof<int>) (v.Type) "for" (SR.GetString(SR.QtmmLoopBodyMustBeLambdaTakingInteger))
        mkFE3 ForIntegerRangeLoopOp (lowerBound, upperBound, mkLambda(v,body))
      
    let mkWhileLoop (guard,body) = 
        checkTypesSR (typeof<bool>) (typeOf guard) "guard" (SR.GetString(SR.QtmmGuardMustBeBool))
        checkTypesSR (typeof<Unit>) (typeOf body) "body" (SR.GetString(SR.QtmmBodyMustBeUnit))
        mkFE2 (WhileLoopOp) (guard,body)
    
    let mkNewDelegate (ty,e) = 
        let mi = getDelegateInvoke ty
        let ps = mi.GetParameters()
        let dlfun = Array.foldBack (fun (p:ParameterInfo) rty -> mkFunTy p.ParameterType rty) ps mi.ReturnType
        checkTypesSR dlfun (typeOf e) "ty" (SR.GetString(SR.QtmmFunTypeNotMatchDelegate))
        mkFE1 (NewDelegateOp ty) e
    
    let mkLet (v,e,b) = 
        checkBind (v,e);
        mkLetRaw (e,mkLambda(v,b))

    //let mkLambdas(vs,b) = mkRLinear mkLambdaRaw (vs,(b:>Expr))
    let mkTupledApplication (f,args) = 
        match args with 
        | [] -> mkApplication (f,mkUnit())
        | [x] -> mkApplication (f,x)
        | _ -> mkApplication (f,mkNewTuple args)
        
    let mkApplications(f: Expr,es:list<list<Expr>>) = mkLLinear mkTupledApplication (f,es)
    
    let mkIteratedLambdas(vs,b) = mkRLinear  mkLambda (vs,b)
    
    let mkLetRecRaw v = mkFE1 LetRecOp v
    let mkLetRecCombRaw v = mkFEN LetRecCombOp v
    let mkLetRec (ves:(Var*Expr) list,body) = 
        List.iter checkBind ves;
        let vs,es = List.unzip ves 
        mkLetRecRaw(mkIteratedLambdas (vs,mkLetRecCombRaw (body::es)))

    let ReflectedDefinitionsResourceNameBase = "ReflectedDefinitions"

    //-------------------------------------------------------------------------
    // General Method Binder

    let typeEquals     (s:Type) (t:Type) = s.Equals(t)
    let typesEqual (ss:Type list) (tt:Type list) =
      (ss.Length = tt.Length) && List.forall2 typeEquals ss tt

    let instFormal (typarEnv: Type[]) (ty:Instantiable<'T>) = ty (fun i -> typarEnv.[i])

    let getGenericArguments(tc:Type) = 
        if tc.IsGenericType then tc.GetGenericArguments() else [| |] 

    let getNumGenericArguments(tc:Type) = 
        if tc.IsGenericType then tc.GetGenericArguments().Length else 0
    
    let bindMethodBySearch (parentT:Type,nm,marity,argtys,rty) =
        let methInfos = parentT.GetMethods(staticOrInstanceBindingFlags) |> Array.toList 
        // First, filter on name, if unique, then binding "done" 
        let tyargTs = getGenericArguments(parentT) 
        let methInfos = methInfos |> List.filter (fun methInfo -> methInfo.Name = nm)
        match methInfos with 
        | [methInfo] -> 
            methInfo
        | _ ->
            // Second, type match. 
            let select (methInfo:MethodInfo) =
                // mref implied Types 
                let mtyargTIs = if methInfo.IsGenericMethod then methInfo.GetGenericArguments() else [| |] 
                if mtyargTIs.Length  <> marity then false (* method generic arity mismatch *) else
                let typarEnv = (Array.append tyargTs mtyargTIs) 
                let argTs = argtys |> List.map (instFormal typarEnv) 
                let resT  = instFormal typarEnv rty 
                
                // methInfo implied Types 
                let haveArgTs = 
                    let parameters = Array.toList (methInfo.GetParameters()) 
                    parameters |> List.map (fun param -> param.ParameterType) 
                let haveResT  = methInfo.ReturnType 
                // check for match 
                if argTs.Length <> haveArgTs.Length then false (* method argument length mismatch *) else
                let res = typesEqual (resT::argTs) (haveResT::haveArgTs) 
                res
            // return MethodInfo for (generic) type's (generic) method 
            match List.tryFind select methInfos with
            | None          -> raise <| System.InvalidOperationException (SR.GetString SR.QcannotBindToMethod) 
            | Some methInfo -> methInfo 

    let bindMethodHelper (parentT: Type, nm,marity,argtys,rty) =
      if parentT = null then invalidArg "parentT" (SR.GetString(SR.QparentCannotBeNull))
      if marity = 0 then 
          let tyargTs = if parentT.IsGenericType then parentT.GetGenericArguments() else [| |] 
          let argTs = Array.ofList (List.map (instFormal tyargTs) argtys) 
          let resT  = instFormal tyargTs rty 
          let methInfo = 
              try 
                 match parentT.GetMethod(nm,staticOrInstanceBindingFlags,null,argTs,null) with 
                 | null -> None
                 | res -> Some(res)
               with :? AmbiguousMatchException -> None 
          match methInfo with 
          | Some methInfo when (typeEquals resT methInfo.ReturnType) -> methInfo
          | _ -> bindMethodBySearch(parentT,nm,marity,argtys,rty)
      else 
          bindMethodBySearch(parentT,nm,marity,argtys,rty)

    let bindModuleProperty (ty:Type,nm) = 
        match ty.GetProperty(nm,staticBindingFlags) with
        | null -> raise <| System.InvalidOperationException (SR.GetString2(SR.QcannotBindProperty, nm, ty.ToString()))
        | res -> res

            
    let bindModuleFunction (ty:Type,nm) = 
        match ty.GetMethod(nm,staticBindingFlags) with 
        | null -> raise <| System.InvalidOperationException (SR.GetString2(SR.QcannotBindFunction, nm, ty.ToString()))
        | res -> res
            
    let mkNamedType (tc:Type,tyargs)  =
        match  tyargs with 
        | [] -> tc
        | _ -> tc.MakeGenericType(Array.ofList tyargs)

    let inline checkNonNullResult (arg:string,err:string) y = 
        match box y with 
        | null -> raise (new ArgumentNullException(arg,err)) 
        | _ -> y

    let inst (tyargs:Type list) (i: Instantiable<'T>) = i (fun idx -> tyargs.[idx]) // Note, O(n) looks, but #tyargs is always small
    
    let bindProp (tc,propName,retType,argTypes,tyargs) =
        // We search in the instantiated type, rather than searching the generic type.
        let typ = mkNamedType(tc,tyargs)
        let argtyps : Type list = argTypes |> inst tyargs
        let retType : Type = retType |> inst tyargs |> removeVoid
        typ.GetProperty(propName, staticOrInstanceBindingFlags, null, retType, Array.ofList argtyps, null) |> checkNonNullResult ("propName", SR.GetString1(SR.QfailedToBindProperty, propName)) // fxcop may not see "propName" as an arg

    let bindField (tc,fldName,tyargs) =
        let typ = mkNamedType(tc,tyargs)
        typ.GetField(fldName,staticOrInstanceBindingFlags) |> checkNonNullResult ("fldName", SR.GetString1(SR.QfailedToBindField, fldName))  // fxcop may not see "fldName" as an arg

    let bindGenericCtor (tc:Type,argTypes:Instantiable<Type list>) =
        let argtyps =  instFormal (getGenericArguments tc) argTypes
        tc.GetConstructor(instanceBindingFlags,null,Array.ofList argtyps,null) |> checkNonNullResult ("tc", SR.GetString(SR.QfailedToBindConstructor))  // fxcop may not see "tc" as an arg

    let bindCtor (tc,argTypes:Instantiable<Type list>,tyargs) =
        let typ = mkNamedType(tc,tyargs)
        let argtyps = argTypes |> inst tyargs
        typ.GetConstructor(instanceBindingFlags,null,Array.ofList argtyps,null) |> checkNonNullResult ("tc", SR.GetString(SR.QfailedToBindConstructor)) // fxcop may not see "tc" as an arg

    let chop n xs =
        if n < 0 then invalidArg "n" (SR.GetString(SR.inputMustBeNonNegative))
        let rec split l = 
            match l with 
            | 0,xs    -> [],xs
            | n,x::xs -> let front,back = split (n-1,xs)
                         x::front,back
            | _,[]    -> failwith "List.chop: not enough elts list"
        split (n,xs)

    let instMeth (ngmeth: MethodInfo, methTypeArgs) = 
        if ngmeth.GetGenericArguments().Length = 0 then ngmeth(* non generic *) 
        else ngmeth.MakeGenericMethod(Array.ofList methTypeArgs) 

    let bindGenericMeth (tc:Type,argTypes : list<Instantiable<Type>>,retType,methName,numMethTyargs) =
        bindMethodHelper(tc,methName,numMethTyargs,argTypes,retType) 

    let bindMeth ((tc:Type,argTypes : list<Instantiable<Type>>,retType,methName,numMethTyargs),tyargs) =
        let ntyargs = tc.GetGenericArguments().Length 
        let enclTypeArgs,methTypeArgs = chop ntyargs tyargs
        let ty = mkNamedType(tc,enclTypeArgs)
        let ngmeth = bindMethodHelper(ty,methName,numMethTyargs,argTypes,retType) 
        instMeth(ngmeth,methTypeArgs)

    let pinfoIsStatic (pinfo:PropertyInfo) = 
        if pinfo.CanRead then pinfo.GetGetMethod(true).IsStatic
        elif pinfo.CanWrite then pinfo.GetSetMethod(true).IsStatic
        else false
        
    //--------------------------------------------------------------------------
    // Unpickling
    //--------------------------------------------------------------------------

    module SimpleUnpickle = 

        [<NoEquality; NoComparison>]
        type InputState = 
          { is: ByteStream; 
            istrings: string array;
            localAssembly: System.Reflection.Assembly  }

        let u_byte_as_int st = st.is.ReadByte() 

        let u_bool st = let b = u_byte_as_int st in (b = 1) 
        let u_void (_: InputState) = ()
        let u_unit (_: InputState) = ()
        let prim_u_int32 st = 
            let b0 =  (u_byte_as_int st)
            let b1 =  (u_byte_as_int st)
            let b2 =  (u_byte_as_int st)
            let b3 =  (u_byte_as_int st)
            b0 ||| (b1 <<< 8) ||| (b2 <<< 16) ||| (b3 <<< 24)

        let u_int32 st = 
            let b0 = u_byte_as_int st 
            if b0 <= 0x7F then b0 
            elif b0 <= 0xbf then 
                let b0 = b0 &&& 0x7f 
                let b1 = (u_byte_as_int st) 
                (b0 <<< 8) ||| b1
            else  
                prim_u_int32 st

        let u_bytes st = 
            let n = u_int32 st 
            st.is.ReadBytes(n)

        let prim_u_string st = 
            let len =  (u_int32 st) 
            st.is.ReadUtf8BytesAsString(len)

        let u_int    st = u_int32 st
        let u_sbyte  st = sbyte (u_int32 st)
        let u_byte   st = byte (u_byte_as_int st)
        let u_int16  st = int16 (u_int32 st)
        let u_uint16 st = uint16 (u_int32 st)
        let u_uint32 st = uint32 (u_int32 st)
        let u_int64  st = 
            let b1 = int64 (u_int32 st) &&& 0xFFFFFFFFL 
            let b2 = int64 (u_int32 st) 
            b1 ||| (b2 <<< 32)
        let u_uint64  st = uint64 (u_int64 st)
        let u_double st = System.BitConverter.ToDouble(System.BitConverter.GetBytes(u_int64 st),0)
        let u_float32 st = System.BitConverter.ToSingle(System.BitConverter.GetBytes(u_int32 st),0)
        let u_char st = char (int32 (u_uint16 st))
        let inline u_tup2 p1 p2 st = let a = p1 st in let b = p2 st in (a,b)
        let inline u_tup3 p1 p2 p3 st =
            let a = p1 st in let b = p2 st in let c = p3 st in (a,b,c)
        let inline u_tup4 p1 p2 p3 p4 st =
            let a = p1 st in let b = p2 st in let c = p3 st in let d = p4 st in (a,b,c,d)
        let inline u_tup5 p1 p2 p3 p4 p5 st =
            let a = p1 st in let b = p2 st in let c = p3 st in let d = p4 st in let e = p5 st in (a,b,c,d,e)
        let u_uniq (tbl: _ array) st = 
            let n = u_int st 
            if n < 0 || n >= tbl.Length then failwith ("u_uniq: out of range, n = "+string n+ ", sizeof(tab) = " + string tbl.Length); 
            tbl.[n]
        let u_string st = u_uniq st.istrings st

        let rec u_list_aux f acc st = 
            let tag = u_byte_as_int st 
            match tag with
            | 0 -> List.rev acc
            | 1 -> let a = f st in u_list_aux f (a::acc) st 
            | n -> failwith ("u_list: found number " + string n)
        let u_list f st = u_list_aux f [] st
         
        let unpickle_obj localAssembly u phase2bytes =
            let phase2data = 
                let st2 = 
                   { is = new ByteStream(phase2bytes,0,phase2bytes.Length); 
                     istrings = [| |];
                     localAssembly=localAssembly }
                u_tup2 (u_list prim_u_string) u_bytes st2 
            let stringTab,phase1bytes = phase2data 
            let st1 = 
               { is = new ByteStream(phase1bytes,0,phase1bytes.Length); 
                 istrings = Array.ofList stringTab;
                   localAssembly=localAssembly } 
            let res = u st1 
            res 

    open SimpleUnpickle

    let decodeFunTy args =
        match args with 
        | [d;r] -> funTyC.MakeGenericType([| d; r |])
        | _ -> invalidArg "args" (SR.GetString(SR.QexpectedTwoTypes))

    let decodeArrayTy n (tys: Type list) = 
        match tys with
        | [ty] -> if (n = 1) then ty.MakeArrayType() else ty.MakeArrayType(n)  
                  // typeof<int>.MakeArrayType(1) returns "Int[*]" but we need "Int[]"
        | _ -> invalidArg "tys" (SR.GetString(SR.QexpectedOneType))
        
    let mkNamedTycon (tcName,ass:Assembly) =
        match ass.GetType(tcName) with 
        | null  -> 
            // For some reason we can get 'null' returned here even when a type with the right name exists... Hence search the slow way...
            match (ass.GetTypes() |> Array.tryFind (fun a -> a.FullName = tcName)) with 
            | Some ty -> ty
            | None -> invalidArg "tcName" (SR.GetString2(SR.QfailedToBindTypeInAssembly, tcName, ass.FullName)) // "Available types are:\n%A" tcName ass (ass.GetTypes() |> Array.map (fun a -> a.FullName))
        | ty -> ty

    let decodeNamedTy tc tsR = mkNamedType(tc,tsR)

    let mscorlib = typeof<System.Int32>.Assembly
    let u_assref st = 
        let a = u_string st 
        if a = "" then mscorlib
        elif a = "." then st.localAssembly 
        else 
            match System.Reflection.Assembly.Load(a) with 
            | null -> raise <| System.InvalidOperationException(SR.GetString1(SR.QfailedToBindAssembly, a.ToString()))
            | ass -> ass
        
    let u_NamedType st = 
        let a,b = u_tup2 u_string u_assref st 
        mkNamedTycon (a,b)

    let u_tyconstSpec st = 
      let tag = u_byte_as_int st 
      match tag with 
      | 1 -> u_unit             st |> (fun () -> decodeFunTy) 
      | 2 -> u_NamedType st |> decodeNamedTy 
      | 3 -> u_int              st |> decodeArrayTy
      | _ -> failwith "u_tyconstSpec" 

    let appL fs env = List.map (fun f -> f env) fs
    
    let rec u_dtype st : (int -> Type) -> Type = 
      let tag = u_byte_as_int st 
      match tag with 
      | 0 -> u_int                              st |> (fun x env     -> env(x)) 
      | 1 -> u_tup2 u_tyconstSpec (u_list u_dtype) st |> (fun (a,b) env -> a (appL b env))
      | _ -> failwith "u_dtype" 

    let u_dtypes st = let a = u_list u_dtype st in appL a 

    let (|NoTyArgs|) = function [] -> () | _ -> failwith "incorrect number of arguments during deserialization"
    let (|OneTyArg|) = function [x] -> x | _ -> failwith "incorrect number of arguments during deserialization"
    
    [<NoEquality; NoComparison>]
    type BindingEnv = 
        { vars : Map<int,Var>; 
          varn: int; 
          typeInst : int -> Type }

    let addVar env v = 
        { env with vars = env.vars.Add(env.varn,v); varn=env.varn+1 }

    let envClosed (types:Type[])  =
        { vars = Map.empty; 
          varn = 0;
          typeInst = fun (n:int) -> types.[n] }

    type Bindable<'T> = BindingEnv -> 'T
    
    let rec u_Expr st = 
        let tag = u_byte_as_int st 
        match tag with 
        | 0 -> u_tup3 u_constSpec u_dtypes (u_list u_Expr) st 
                |> (fun (a,b,args) (env:BindingEnv) -> 
                    let tyargs = b env.typeInst 
                    E(CombTerm(a tyargs, List.map (fun e -> e env) args ))) 
        | 1 -> let x = u_VarRef st 
               (fun env -> E(VarTerm (x env)))
        | 2 -> let a = u_VarDecl st
               let b = u_Expr st
               (fun env -> let v = a env in E(LambdaTerm(v,b (addVar env v))))
        | 3 -> let a = u_dtype st
               let idx = u_int st
               (fun env -> E(HoleTerm(a env.typeInst , idx)))
        | 4 -> let a = u_Expr st
               (fun env -> mkQuote(a env))
        | 5 -> let a = u_Expr st
               let attrs = u_list u_Expr st
               (fun env -> let e = (a env) in EA(e.Tree,(e.CustomAttributes @ List.map (fun attrf -> attrf env) attrs)))
        | 6 -> let a = u_dtype st
               (fun env -> mkVar(Var.Global("this", a env.typeInst)))
        | _ -> failwith "u_Expr"
    and u_VarDecl st = 
        let s,b,mut = u_tup3 u_string u_dtype u_bool st 
        (fun env -> new Var(s, b env.typeInst, mut))
    and u_VarRef st = 
        let i = u_int st 
        (fun env -> env.vars.[i])
    and u_RecdField st = 
        let ty,nm = u_tup2 u_NamedType u_string st  
        (fun tyargs -> getRecordProperty(mkNamedType(ty,tyargs),nm)) 
    and u_UnionCaseInfo st = 
        let ty,nm = u_tup2 u_NamedType u_string st  
        (fun tyargs -> getUnionCaseInfo(mkNamedType(ty,tyargs),nm)) 

    and u_UnionCaseField st = 
        let case,i = u_tup2 u_UnionCaseInfo u_int st  
        (fun tyargs -> getUnionCaseInfoField(case tyargs,i))

    and u_ModuleDefn st = 
        let (ty,nm,isProp) = u_tup3 u_NamedType u_string u_bool st 
        if isProp then StaticPropGetOp(bindModuleProperty(ty,nm)) 
        else StaticMethodCallOp(bindModuleFunction(ty,nm))

    and u_MethodInfoData st = 
        u_tup5 u_NamedType (u_list u_dtype) u_dtype u_string u_int st
            
    and u_PropInfoData st = 
        u_tup4 u_NamedType u_string u_dtype u_dtypes  st
        
    and u_CtorInfoData st =
        u_tup2 u_NamedType u_dtypes st
    
    and u_MethodBase st = 
        let tag = u_byte_as_int st 
        match tag with 
        | 0 -> 
            match u_ModuleDefn st with 
            | StaticMethodCallOp(minfo) -> (minfo :> MethodBase)
            | StaticPropGetOp(pinfo) -> (pinfo.GetGetMethod(true) :> MethodBase)
            | _ -> failwith "unreachable"
        | 1 -> 
            let data = u_MethodInfoData st
            let minfo = bindGenericMeth(data) in 
            (minfo :> MethodBase)
        | 2 -> 
            let data = u_CtorInfoData st
            let cinfo = bindGenericCtor(data) in 
            (cinfo :> MethodBase)
        | _ -> failwith "u_MethodBase" 

      
    and u_constSpec st = 
        let tag = u_byte_as_int st 
        match tag with 
        | 0 -> u_void       st |> (fun () NoTyArgs -> IfThenElseOp)
        | 1 -> u_ModuleDefn st  |> (fun op tyargs -> 
                                        match op with 
                                        | StaticMethodCallOp(minfo) -> StaticMethodCallOp(instMeth(minfo,tyargs))
                                        // OK to throw away the tyargs here since this only non-generic values in modules get represented by static properties
                                        | op -> op)
        | 2 -> u_void            st |> (fun () NoTyArgs -> LetRecOp)
        | 3 -> u_NamedType        st |> (fun x tyargs -> NewRecordOp (mkNamedType(x,tyargs)))
        | 4 -> u_RecdField       st |> (fun prop tyargs -> InstancePropGetOp(prop tyargs))
        | 5 -> u_UnionCaseInfo   st |> (fun unionCase tyargs -> NewUnionCaseOp(unionCase tyargs))
        | 6 -> u_UnionCaseField  st |> (fun prop tyargs -> InstancePropGetOp(prop tyargs) )
        | 7 -> u_UnionCaseInfo   st |> (fun unionCase tyargs -> UnionCaseTestOp(unionCase tyargs))
        | 8 -> u_void          st |> (fun () (OneTyArg(tyarg)) -> NewTupleOp tyarg)
        | 9 -> u_int           st |> (fun x (OneTyArg(tyarg)) -> TupleGetOp (tyarg,x))
        // Note, these get type args because they may be the result of reading literal field constants
        | 11 -> u_bool         st |> (fun x (OneTyArg(tyarg)) -> mkLiftedValueOpG (x, tyarg))
        | 12 -> u_string       st |> (fun x (OneTyArg(tyarg)) -> mkLiftedValueOpG (x, tyarg))
        | 13 -> u_float32      st |> (fun x (OneTyArg(tyarg)) -> mkLiftedValueOpG (x, tyarg))
        | 14 -> u_double       st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 15 -> u_char         st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 16 -> u_sbyte        st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 17 -> u_byte         st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 18 -> u_int16        st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 19 -> u_uint16       st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 20 -> u_int32        st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 21 -> u_uint32       st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 22 -> u_int64        st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 23 -> u_uint64       st |> (fun a (OneTyArg(tyarg)) -> mkLiftedValueOpG (a, tyarg))
        | 24 -> u_void         st |> (fun () NoTyArgs -> mkLiftedValueOpG ((), typeof<unit>))
        | 25 -> u_PropInfoData st |> (fun (a,b,c,d) tyargs -> let pinfo = bindProp(a,b,c,d,tyargs) in if pinfoIsStatic pinfo then StaticPropGetOp(pinfo) else InstancePropGetOp(pinfo))
        | 26 -> u_CtorInfoData st |> (fun (a,b) tyargs  -> NewObjectOp (bindCtor(a,b,tyargs)))
        | 28 -> u_void         st |> (fun () (OneTyArg(ty)) -> CoerceOp ty)
        | 29 -> u_void         st |> (fun () NoTyArgs -> SequentialOp)
        | 30 -> u_void         st |> (fun () NoTyArgs -> ForIntegerRangeLoopOp)
        | 31 -> u_MethodInfoData st |> (fun p tyargs -> let minfo = bindMeth(p,tyargs) in if minfo.IsStatic then StaticMethodCallOp(minfo) else InstanceMethodCallOp(minfo))
        | 32 -> u_void           st |> (fun () (OneTyArg(ty)) -> NewArrayOp ty)
        | 33 -> u_void           st |> (fun () (OneTyArg(ty)) -> NewDelegateOp ty)
        | 34 -> u_void           st |> (fun () NoTyArgs -> WhileLoopOp)
        | 35 -> u_void           st |> (fun () NoTyArgs -> LetOp)
        | 36 -> u_RecdField      st |> (fun prop tyargs -> InstancePropSetOp(prop tyargs))
        | 37 -> u_tup2 u_NamedType u_string st |> (fun (a,b) tyargs -> let finfo = bindField(a,b,tyargs) in if finfo.IsStatic then StaticFieldGetOp(finfo) else InstanceFieldGetOp(finfo))
        | 38 -> u_void           st |> (fun () NoTyArgs -> LetRecCombOp)
        | 39 -> u_void           st |> (fun () NoTyArgs -> AppOp)
        | 40 -> u_void           st |> (fun () (OneTyArg(ty)) -> ValueOp(null,ty))
        | 41 -> u_void           st |> (fun () (OneTyArg(ty)) -> DefaultValueOp(ty))
        | 42 -> u_PropInfoData   st |> (fun (a,b,c,d) tyargs -> let pinfo = bindProp(a,b,c,d,tyargs) in if pinfoIsStatic pinfo then StaticPropSetOp(pinfo) else InstancePropSetOp(pinfo))
        | 43 -> u_tup2 u_NamedType u_string st |> (fun (a,b) tyargs -> let finfo = bindField(a,b,tyargs) in if finfo.IsStatic then StaticFieldSetOp(finfo) else InstanceFieldSetOp(finfo))
        | 44 -> u_void           st |> (fun () NoTyArgs -> AddressOfOp)
        | 45 -> u_void           st |> (fun () NoTyArgs -> AddressSetOp)
        | 46 -> u_void           st |> (fun () (OneTyArg(ty)) -> TypeTestOp(ty))
        | 47 -> u_void           st |> (fun () NoTyArgs -> TryFinallyOp)
        | 48 -> u_void           st |> (fun () NoTyArgs -> TryWithOp)
        | 49 -> u_void           st |> (fun () NoTyArgs -> VarSetOp)
        | _ -> failwithf "u_constSpec, unrecognized tag %d" tag
    let u_ReflectedDefinition = u_tup2 u_MethodBase u_Expr
    let u_ReflectedDefinitions = u_list u_ReflectedDefinition

    let unpickleExpr (localType : System.Type) = unpickle_obj localType.Assembly u_Expr 

    let unpickleReflectedDefns (localAssembly : System.Reflection.Assembly) = unpickle_obj localAssembly u_ReflectedDefinitions

    //--------------------------------------------------------------------------
    // General utilities that will eventually be folded into 
    // Microsoft.FSharp.Quotations.Typed
    //--------------------------------------------------------------------------
    
    /// Fill the holes in an Expr 
    let rec fillHolesInRawExpr (l:Expr[]) (E t as e) = 
        match t with 
        | VarTerm _ -> e
        | LambdaTerm (v,b) -> EA(LambdaTerm(v, fillHolesInRawExpr l b ),e.CustomAttributes)
        | CombTerm   (op,args) -> EA(CombTerm(op, args |> List.map (fillHolesInRawExpr l)),e.CustomAttributes)
        | HoleTerm   (ty,idx) ->  
           if idx < 0 || idx >= l.Length then failwith "hole index out of range";
           let h = l.[idx]
           checkTypesSR (typeOf h) ty "fill" (SR.GetString(SR.QtmmRaw))
           h

    let rec freeInExprAcc bvs acc (E t) = 
        match t with 
        | HoleTerm   _  -> acc
        | CombTerm (_, ag) -> ag |> List.fold (freeInExprAcc bvs) acc
        | VarTerm    v -> if Set.contains v bvs || Set.contains v acc then acc else Set.add v acc
        | LambdaTerm (v,b) -> freeInExprAcc (Set.add v bvs) acc b
    and freeInExpr e = freeInExprAcc Set.empty Set.empty e

    // utility for folding
    let foldWhile f st (ie: seq<'T>)  = 
        use e = ie.GetEnumerator()
        let mutable res = Some st
        while (res.IsSome && e.MoveNext()) do
            res <-  f (match res with Some a -> a | _ -> failwith "internal error") e.Current;
        res      
    
    let mkTyparSubst (tyargs:Type[]) =
        let n = tyargs.Length 
        fun idx -> 
          if idx < n then tyargs.[idx]
          else raise <| System.InvalidOperationException (SR.GetString(SR.QtypeArgumentOutOfRange))

    [<NoEquality; NoComparison>]
    exception Clash of Var

    /// Replace type variables and expression variables with parameters using the
    /// given substitution functions/maps.  
    let rec substituteInExpr bvs tmsubst (E t as e) = 
        match t with 
        | CombTerm (c, args) -> 
            let substargs = args |> List.map (fun arg -> substituteInExpr bvs tmsubst arg) 
            EA(CombTerm(c, substargs),e.CustomAttributes)
        | VarTerm    v -> 
            match tmsubst v with 
            | None -> e 
            | Some e2 -> 
                let fvs = freeInExpr e2 
                let clashes = Set.intersect fvs bvs in
                if clashes.IsEmpty then e2
                else raise (Clash(clashes.MinimumElement)) 
        | LambdaTerm (v,b) -> 
             try EA(LambdaTerm(v,substituteInExpr (Set.add v bvs) tmsubst b),e.CustomAttributes)
             with Clash(bv) ->
                 if v = bv then
                     let v2 = new Var(v.Name,v.Type)
                     let v2exp = E(VarTerm(v2))
                     EA(LambdaTerm(v2,substituteInExpr bvs (fun v -> if v = bv then Some(v2exp) else tmsubst v) b),e.CustomAttributes)
                 else
                     reraise()
        | HoleTerm _ -> e


    let substituteRaw tmsubst e = substituteInExpr Set.empty tmsubst e 

    let readToEnd (s : Stream) = 
        let n = int s.Length 
        let res = Array.zeroCreate n 
        let i = ref 0 
        while (!i < n) do 
          i := !i + s.Read(res,!i,(n - !i)) 
        done;
        res 

    let decodedTopResources = new Dictionary<Assembly * string, int>(10,HashIdentity.Structural)

#if FX_NO_REFLECTION_MODULE_HANDLES // not available on Silverlight
    type ModuleHandle = string
    type System.Reflection.Module with 
        member x.ModuleHandle = x.FullyQualifiedName
#else
    type ModuleHandle = System.ModuleHandle
#endif
   
#if FX_NO_REFLECTION_METADATA_TOKENS // not available on Compact Framework
    [<StructuralEquality; NoComparison>]
    type ReflectedDefinitionTableKey = 
        | Key of Type * int * Type[]
        static member GetKey(methodBase:MethodBase) = 
            Key(methodBase.DeclaringType.Module.ModuleHandle,
                (if methodBase.IsGenericMethod then methodBase.GetGenericArguments().Length else 0), 
                methodBase.GetParameters() |> Array.map (fun p -> p.Type))
#else
    [<StructuralEquality; NoComparison>]
    type ReflectedDefinitionTableKey = 
        | Key of ModuleHandle * int
        static member GetKey(methodBase:MethodBase) = 
            Key(methodBase.Module.ModuleHandle,methodBase.MetadataToken)
#endif

    [<NoEquality; NoComparison>]
    type ReflectedDefinitionTableEntry = Entry of Bindable<Expr>

    let reflectedDefinitionTable = new Dictionary<ReflectedDefinitionTableKey,ReflectedDefinitionTableEntry>(10,HashIdentity.Structural)

    let registerReflectedDefinitions (assem: Assembly, rn, bytes: byte[]) =
        let defns = unpickleReflectedDefns assem  bytes 
        defns |> List.iter (fun (minfo,e) -> 
            //printfn "minfo = %A, handle = %A, token = %A" minfo minfo.Module.ModuleHandle minfo.MetadataToken
            let key = ReflectedDefinitionTableKey.GetKey minfo
            lock reflectedDefinitionTable (fun () -> 
                //printfn "Adding %A, hc = %d" key (key.GetHashCode());
                reflectedDefinitionTable.Add(key,Entry(e))));
        //System.Console.WriteLine("Added {0} resource {1}", assem.FullName, rn);
        decodedTopResources.Add((assem,rn),0)

    let resolveMethodBase (methodBase: MethodBase, tyargs: Type []) =
        checkNonNull "methodBase" methodBase
        let data = 
            let assem = methodBase.DeclaringType.Assembly
            let key = ReflectedDefinitionTableKey.GetKey methodBase
            //printfn "Looking for %A, hc = %d, hc2 = %d" key (key.GetHashCode()) (assem.GetHashCode());
            let ok,res = lock reflectedDefinitionTable (fun () -> reflectedDefinitionTable.TryGetValue(key))
            if ok then Some(res) else
            //System.Console.WriteLine("Loading {0}", td.Assembly);
            let qdataResources = 
#if FX_NO_REFLECTION_EMIT
#else
                // dynamic assemblies don't support the GetManifestResourceNames 
                match assem with 
                | :? System.Reflection.Emit.AssemblyBuilder -> []
                | _ -> 
#endif
                    (try assem.GetManifestResourceNames()  
                     // This raises NotSupportedException for dynamic assemblies
                     with :? NotSupportedException -> [| |])
                    |> Array.toList 
                    |> List.filter (fun rn -> 
                          //System.Console.WriteLine("Considering resource {0}", rn);
                          rn.StartsWith(ReflectedDefinitionsResourceNameBase,StringComparison.Ordinal) &&
                          not (decodedTopResources.ContainsKey((assem,rn)))) 
                    |> List.map (fun rn -> rn,unpickleReflectedDefns assem (readToEnd (assem.GetManifestResourceStream(rn)))) 
                
            // ok, add to the table
            let ok,res = 
                lock reflectedDefinitionTable (fun () -> 
                     // check another thread didn't get in first
                     if not (reflectedDefinitionTable.ContainsKey(key)) then
                         qdataResources 
                         |> List.iter (fun (rn,defns) ->
                             defns |> List.iter (fun (methodBase,e) -> 
                                reflectedDefinitionTable.[ReflectedDefinitionTableKey.GetKey methodBase] <- Entry(e));
                             decodedTopResources.Add((assem,rn),0))
                     // we know it's in the table now, if it's ever going to be there
                     reflectedDefinitionTable.TryGetValue(key) 
                );

            if ok then Some(res) else None

        match data with 
        | Some (Entry(exprBuilder)) -> 
            let expectedNumTypars = 
                getNumGenericArguments(methodBase.DeclaringType) + 
                (match methodBase with 
                 | :? MethodInfo as minfo -> if minfo.IsGenericMethod then minfo.GetGenericArguments().Length else 0
                 | _ -> 0)
            if (expectedNumTypars <> tyargs.Length) then 
                invalidArg "tyargs" (SR.GetString3(SR.QwrongNumOfTypeArgs, methodBase.Name, expectedNumTypars.ToString(), tyargs.Length.ToString()));
            Some(exprBuilder {typeInst = mkTyparSubst tyargs; vars=Map.empty; varn=0})
        | None -> None

    let resolveMethodBaseInstantiated (methodBase:MethodBase) = 
        checkNonNull "methodBase" methodBase
        match methodBase with 
        | :? MethodInfo as minfo -> 
               let tyargs = 
                   Array.append
                       (getGenericArguments minfo.DeclaringType)
                       (if minfo.IsGenericMethod then minfo.GetGenericArguments() else [| |])
               resolveMethodBase (methodBase, tyargs)
        | :? ConstructorInfo as cinfo -> 
               let tyargs = getGenericArguments cinfo.DeclaringType
               resolveMethodBase (methodBase, tyargs)
        | _ -> 
               resolveMethodBase (methodBase, [| |])

    let deserialize (localAssembly, types, splices, bytes) : Expr = 
        let expr = unpickleExpr localAssembly bytes (envClosed  (Array.ofList types))
        fillHolesInRawExpr (Array.ofList splices) expr
        
  
    let cast (expr: Expr) : Expr<'T> = 
        checkTypesSR  (typeof<'T>) (typeOf expr)  "expr" (SR.GetString(SR.QtmmExprHasWrongType))
        new Expr<'T>(expr.Tree,expr.CustomAttributes)

open Patterns


type Expr with 
    member x.Substitute f = substituteRaw f x
    member x.GetFreeVars ()  = (freeInExpr x :> seq<_>)
    member x.Type = typeOf x 

    static member AddressOf (e:Expr) = 
        mkAddressOf e    

    static member AddressSet (e1:Expr, e2:Expr) = 
        mkAddressSet (e1,e2)

    static member Application (e1:Expr, e2:Expr) = 
        mkApplication (e1,e2)

    static member Applications (f:Expr, es) = 
        mkApplications (f, es)

    static member Call (methodInfo:MethodInfo, args) = 
        checkNonNull "methodInfo" methodInfo
        mkStaticMethodCall (methodInfo, args)

    static member Call (obj:Expr,methodInfo:MethodInfo, args) = 
        checkNonNull "methodInfo" methodInfo
        mkInstanceMethodCall (obj,methodInfo,args)

    static member Coerce (e:Expr, target:Type) = 
        checkNonNull "target" target
        mkCoerce (target, e)

    static member IfThenElse (g:Expr, t:Expr, e:Expr) = 
        mkIfThenElse (g, t, e)

    static member ForIntegerRangeLoop (v, start:Expr, finish:Expr, body:Expr) = 
        mkForLoop(v, start, finish, body)
    //static member Range: Expr * Expr -> Expr 
    //static member RangeStep: Expr * Expr * Expr -> Expr 

    static member FieldGet (fieldInfo:FieldInfo) = 
        checkNonNull "fieldInfo" fieldInfo
        mkStaticFieldGet fieldInfo

    static member FieldGet (obj:Expr, fieldInfo:FieldInfo) = 
        checkNonNull "fieldInfo" fieldInfo
        mkInstanceFieldGet (obj, fieldInfo)
    
    static member FieldSet (fieldInfo:FieldInfo, v:Expr) = 
        checkNonNull "fieldInfo" fieldInfo
        mkStaticFieldSet (fieldInfo, v)
    
    static member FieldSet (obj:Expr, fieldInfo:FieldInfo, v:Expr) = 
        checkNonNull "fieldInfo" fieldInfo
        mkInstanceFieldSet (obj, fieldInfo, v)

    static member Lambda (v:Var, e:Expr) = mkLambda (v, e)

    static member Let (v:Var,e:Expr,b:Expr) = mkLet (v, e, b)

    static member LetRecursive (binds, e:Expr) = mkLetRec (binds, e)

    static member NewObject (constructorInfo:ConstructorInfo, args) = 
        checkNonNull "constructorInfo" constructorInfo
        mkCtorCall (constructorInfo, args)

    static member DefaultValue (expressionType:Type) = 
        checkNonNull "expressionType" expressionType
        mkDefaultValue expressionType

    static member NewTuple es = 
        mkNewTuple es

    static member NewRecord (recordType:Type, args) = 
        checkNonNull "recordType" recordType
        mkNewRecord (recordType, args)

    static member NewArray (elementType:Type, es) = 
        checkNonNull "elementType" elementType
        mkNewArray(elementType, es)

    static member NewDelegate (delegateType:Type, vs: Var list, body: Expr) = 
        checkNonNull "delegateType" delegateType
        mkNewDelegate(delegateType, mkIteratedLambdas (vs, body))

    static member NewUnionCase (unionCase, es) = 
        mkNewUnionCase (unionCase, es)
    
    static member PropertyGet (obj:Expr, property: PropertyInfo, ?args) = 
        checkNonNull "property" property
        mkInstancePropGet (obj, property, defaultArg args [])

    static member PropertyGet (property: PropertyInfo, ?args) = 
        checkNonNull "property" property
        mkStaticPropGet (property, defaultArg args [])

    static member PropertySet (obj:Expr, property:PropertyInfo, value:Expr, ?args) = 
        checkNonNull "property" property
        mkInstancePropSet(obj, property, defaultArg args [], value)

    static member PropertySet (property:PropertyInfo, value:Expr, ?args) = 
        mkStaticPropSet(property, defaultArg args [], value)

    static member Quote (expr:Expr) = mkQuote expr

    static member Sequential (e1:Expr, e2:Expr) = 
        mkSequential (e1, e2)

    static member TryWith (e1:Expr, v2:Var, e2:Expr, v3:Var, e3:Expr) = 
        mkTryWith (e1, v2, e2, v3, e3)

    static member TryFinally (e1:Expr, e2:Expr) = 
        mkTryFinally (e1, e2)

    static member TupleGet (e:Expr, n:int) = 
        mkTupleGet (typeOf e, n, e)

    static member TypeTest (e: Expr, target: Type) = 
        checkNonNull "target" target
        mkTypeTest (e, target)

    static member UnionCaseTest (e:Expr, unionCase: UnionCaseInfo) = 
        mkUnionCaseTest (unionCase, e)

    static member Value (v:'T) = 
        mkValue (box v, typeof<'T>)

    static member Value(obj: obj, expressionType: Type) = 
        checkNonNull "expressionType" expressionType
        mkValue(obj, expressionType)

    static member Var(v) = 
        mkVar(v)

    static member VarSet (v, e:Expr) = 
        mkVarSet (v, e)

    static member WhileLoop (e1:Expr, e2:Expr) = 
        mkWhileLoop (e1, e2)

    //static member IsInlinedMethodInfo(minfo:MethodInfo) = false
    static member TryGetReflectedDefinition(methodBase:MethodBase) = 
        checkNonNull "methodBase" methodBase
        resolveMethodBaseInstantiated(methodBase)

    static member Cast(expr:Expr) = cast expr

    static member Deserialize(qualifyingType:Type, spliceTypes, spliceExprs, bytes: byte[]) = 
        checkNonNull "qualifyingType" qualifyingType
        checkNonNull "bytes" bytes
        deserialize (qualifyingType, spliceTypes, spliceExprs, bytes)

    static member RegisterReflectedDefinitions(assembly:Assembly, nm, bytes) = 
        checkNonNull "assembly" assembly
        registerReflectedDefinitions(assembly, nm, bytes)

    static member GlobalVar<'T>(name) : Expr<'T> = 
        checkNonNull "name" name
        Expr.Var(Var.Global(name, typeof<'T>)) |> Expr.Cast

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DerivedPatterns =
    open Patterns

    [<CompiledName("BoolPattern")>]
    let (|Bool|_|)          = function ValueObj(:? bool   as v) -> Some(v) | _ -> None
    [<CompiledName("StringPattern")>]
    let (|String|_|)        = function ValueObj(:? string as v) -> Some(v) | _ -> None
    [<CompiledName("SinglePattern")>]
    let (|Single|_|)        = function ValueObj(:? single as v) -> Some(v) | _ -> None
    [<CompiledName("DoublePattern")>]
    let (|Double|_|)        = function ValueObj(:? double as v) -> Some(v) | _ -> None
    [<CompiledName("CharPattern")>]
    let (|Char|_|)          = function ValueObj(:? char   as v) -> Some(v) | _ -> None
    [<CompiledName("SBytePattern")>]
    let (|SByte|_|)         = function ValueObj(:? sbyte  as v) -> Some(v) | _ -> None
    [<CompiledName("BytePattern")>]
    let (|Byte|_|)          = function ValueObj(:? byte   as v) -> Some(v) | _ -> None
    [<CompiledName("Int16Pattern")>]
    let (|Int16|_|)         = function ValueObj(:? int16  as v) -> Some(v) | _ -> None
    [<CompiledName("UInt16Pattern")>]
    let (|UInt16|_|)        = function ValueObj(:? uint16 as v) -> Some(v) | _ -> None
    [<CompiledName("Int32Pattern")>]
    let (|Int32|_|)         = function ValueObj(:? int32  as v) -> Some(v) | _ -> None
    [<CompiledName("UInt32Pattern")>]
    let (|UInt32|_|)        = function ValueObj(:? uint32 as v) -> Some(v) | _ -> None
    [<CompiledName("Int64Pattern")>]
    let (|Int64|_|)         = function ValueObj(:? int64  as v) -> Some(v) | _ -> None
    [<CompiledName("UInt64Pattern")>]
    let (|UInt64|_|)        = function ValueObj(:? uint64 as v) -> Some(v) | _ -> None
    [<CompiledName("UnitPattern")>]
    let (|Unit|_|)          = function Comb0(ValueOp(_,ty)) when ty = typeof<unit> -> Some() | _ -> None

    /// (fun (x,y) -> z) is represented as 'fun p -> let x = p#0 let y = p#1' etc.
    /// This reverses this encoding.
    let (|TupledLambda|_|) (lam: Expr) =
        /// Strip off the 'let' bindings for an TupledLambda
        let rec stripSuccessiveProjLets (p:Var) n expr =
            match expr with 
            | Let(v1,TupleGet(Var(pA),m),rest) 
                  when p = pA && m = n-> 
                      let restvs,b = stripSuccessiveProjLets p (n+1) rest
                      v1::restvs, b
            | _ -> ([],expr)
        match lam.Tree with 
        | LambdaTerm(v,body) ->
              match stripSuccessiveProjLets v 0 body with 
              | [],b -> Some([v], b)
              | letvs,b -> Some(letvs,b)
        | _ -> None

    let (|TupledApplication|_|) e = 
        match e with 
        | Application(f,x) -> 
            match x with 
            | Unit -> Some(f,[])
            | NewTuple(x) -> Some(f,x)
            | x -> Some(f,[x])
        | _ -> None
            
    [<CompiledName("LambdasPattern")>]
    let (|Lambdas|_|) (e: Expr) = qOneOrMoreRLinear (|TupledLambda|_|) e 
    [<CompiledName("ApplicationsPattern")>]
    let (|Applications|_|) (e: Expr) = qOneOrMoreLLinear (|TupledApplication|_|) e 
    /// Reverse the compilation of And and Or
    [<CompiledName("AndAlsoPattern")>]
    let (|AndAlso|_|) x = 
        match x with 
        | IfThenElse(x,y,Bool(false)) -> Some(x,y)
        | _ -> None
        
    [<CompiledName("OrElsePattern")>]
    let (|OrElse|_|) x = 
        match x with 
        | IfThenElse(x,Bool(true),y) -> Some(x,y)
        | _ -> None

    [<CompiledName("SpecificCallPattern")>]
    let (|SpecificCall|_|) templateParameter = 
        // Note: precomputation
        match templateParameter with
        | (Lambdas(_,Call(_,minfo1,_)) | Call(_,minfo1,_)) ->
            let isg1 = minfo1.IsGenericMethod 
            let gmd = if isg1 then minfo1.GetGenericMethodDefinition() else null

            // end-of-precomputation

            (fun tm -> 
               match tm with
               | Call(obj,minfo2,args) 
                  when (minfo1.MetadataToken = minfo2.MetadataToken &&
                        if isg1 then 
                          minfo2.IsGenericMethod && gmd = minfo2.GetGenericMethodDefinition()
                        else
                          minfo1 = minfo2) -> 
                   Some(obj,(minfo2.GetGenericArguments() |> Array.toList),args)
               | _ -> None)
        | _ -> 
            invalidArg "templateParameter" (SR.GetString(SR.QunrecognizedMethodCall))
               

    [<CompiledName("MethodWithReflectedDefinitionPattern")>]
    let (|MethodWithReflectedDefinition|_|) (minfo) = 
        Expr.TryGetReflectedDefinition(minfo)
    
    [<CompiledName("PropertyGetterWithReflectedDefinitionPattern")>]
    let (|PropertyGetterWithReflectedDefinition|_|) (pinfo:System.Reflection.PropertyInfo) = 
        Expr.TryGetReflectedDefinition(pinfo.GetGetMethod(true))

    [<CompiledName("PropertySetterWithReflectedDefinitionPattern")>]
    let (|PropertySetterWithReflectedDefinition|_|) (pinfo:System.Reflection.PropertyInfo) = 
        Expr.TryGetReflectedDefinition(pinfo.GetSetMethod(true))

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ExprShape =
    open Patterns
    let RebuildShapeCombination(a:obj,args) =  
        // preserve the attributes
        let op,attrs = unbox<ExprConstInfo * Expr list>(a)
        let e = 
            match op,args with 
            | AppOp,[f;x]        -> mkApplication(f,x)
            | IfThenElseOp,[g;t;e]     -> mkIfThenElse(g,t,e)
            | LetRecOp,[e1]   -> mkLetRecRaw(e1)     
            | LetRecCombOp,_     -> mkLetRecCombRaw(args) 
            | LetOp,[e1;e2]      -> mkLetRaw(e1,e2)      
            | NewRecordOp(ty),_     -> mkNewRecord(ty, args)
            | NewUnionCaseOp(unionCase),_    -> mkNewUnionCase(unionCase, args)
            | UnionCaseTestOp(unionCase),[arg]  -> mkUnionCaseTest(unionCase,arg)
            | NewTupleOp(ty),_    -> mkNewTupleWithType(ty, args)
            | TupleGetOp(ty,i),[arg] -> mkTupleGet(ty,i,arg)
            | InstancePropGetOp(pinfo),(obj::args)    -> mkInstancePropGet(obj,pinfo,args)
            | StaticPropGetOp(pinfo),[] -> mkStaticPropGet(pinfo,args)
            | InstancePropSetOp(pinfo),obj::v::args -> mkInstancePropSet(obj,pinfo,args,v)
            | StaticPropSetOp(pinfo),v::args -> mkStaticPropSet(pinfo,args,v)
            | InstanceFieldGetOp(finfo),[obj]   -> mkInstanceFieldGet(obj,finfo)
            | StaticFieldGetOp(finfo),[]   -> mkStaticFieldGet(finfo )
            | InstanceFieldSetOp(finfo),[obj;v]   -> mkInstanceFieldSet(obj,finfo,v)
            | StaticFieldSetOp(finfo),[v]   -> mkStaticFieldSet(finfo,v)
            | NewObjectOp minfo,_   -> mkCtorCall(minfo,args)
            | DefaultValueOp(ty),_  -> mkDefaultValue(ty)
            | StaticMethodCallOp(minfo),_ -> mkStaticMethodCall(minfo,args)
            | InstanceMethodCallOp(minfo),obj::args -> mkInstanceMethodCall(obj,minfo,args)
            | CoerceOp(ty),[arg]   -> mkCoerce(ty,arg)
            | NewArrayOp(ty),_    -> mkNewArray(ty,args)
            | NewDelegateOp(ty),[arg]   -> mkNewDelegate(ty,arg)
            | SequentialOp,[e1;e2]     -> mkSequential(e1,e2)
            | TypeTestOp(ty),[e1]     -> mkTypeTest(e1,ty)
            | AddressOfOp,[e1]     -> mkAddressOf(e1)
            | VarSetOp,[E(VarTerm(v)); e]     -> mkVarSet(v,e)
            | AddressSetOp,[e1;e2]     -> mkAddressSet(e1,e2)
            | ForIntegerRangeLoopOp,[e1;e2;E(LambdaTerm(v,e3))]     -> mkForLoop(v,e1,e2,e3)
            | WhileLoopOp,[e1;e2]     -> mkWhileLoop(e1,e2)
            | TryFinallyOp,[e1;e2]     -> mkTryFinally(e1,e2)
            | TryWithOp,[e1;Lambda(v1,e2);Lambda(v2,e3)]     -> mkTryWith(e1,v1,e2,v2,e3)
            | QuoteOp,[e1]     -> mkQuote(e1)
            | ValueOp(v,ty),[]  -> mkValue(v,ty)
            | _ -> raise <| System.InvalidOperationException (SR.GetString(SR.QillFormedAppOrLet))          


        EA(e.Tree,attrs)

    [<CompiledName("ShapePattern")>]
    let rec (|ShapeVar|ShapeLambda|ShapeCombination|) e = 
        let rec loop expr = 
            let (E(t)) = expr 
            match t with 
            | VarTerm v       -> ShapeVar(v)
            | LambdaTerm(v,b) -> ShapeLambda(v,b)
            | CombTerm(op,args) -> ShapeCombination(box<ExprConstInfo * Expr list> (op,expr.CustomAttributes),args)
            | HoleTerm _     -> invalidArg "expr" (SR.GetString(SR.QunexpectedHole))
        loop (e :> Expr)
                
#endif