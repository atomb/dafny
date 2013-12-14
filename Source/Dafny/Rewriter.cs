﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using Bpl = Microsoft.Boogie;

namespace Microsoft.Dafny
{
  [ContractClass(typeof(IRewriterContracts))]
  public interface IRewriter
  {
    void PreResolve(ModuleDefinition m);
    void PostResolve(ModuleDefinition m);
  }
  [ContractClassFor(typeof(IRewriter))]
  abstract class IRewriterContracts : IRewriter
  {
    public void PreResolve(ModuleDefinition m) {
      Contract.Requires(m != null);
    }
    public void PostResolve(ModuleDefinition m) {
      Contract.Requires(m != null);
    }
  }

  public class AutoGeneratedToken : TokenWrapper
  {
    public AutoGeneratedToken(Boogie.IToken wrappedToken)
      : base(wrappedToken)
    {
      Contract.Requires(wrappedToken != null);
    }
  }

  /// <summary>
  /// AutoContracts is an experimental feature that will fill much of the dynamic-frames boilerplate
  /// into a class.  From the user's perspective, what needs to be done is simply:
  ///  - mark the class with {:autocontracts}
  ///  - declare a function (or predicate) called Valid()
  ///  
  /// AutoContracts will then:
  ///
  /// Declare:
  ///    ghost var Repr: set(object);
  ///
  /// For function/predicate Valid(), insert:
  ///    reads this, Repr;
  /// Into body of Valid(), insert (at the beginning of the body):
  ///    this in Repr && null !in Repr
  /// and also insert, for every array-valued field A declared in the class:
  ///    (A != null ==> A in Repr) &&
  /// and for every field F of a class type T where T has a field called Repr, also insert:
  ///    (F != null ==> F in Repr && F.Repr SUBSET Repr && this !in Repr)
  /// Except, if A or F is declared with {:autocontracts false}, then the implication will not
  /// be added.
  ///
  /// For every constructor, add:
  ///    modifies this;
  ///    ensures Valid() && fresh(Repr - {this});
  /// At the end of the body of the constructor, add:
  ///    Repr := {this};
  ///    if (A != null) { Repr := Repr + {A}; }
  ///    if (F != null) { Repr := Repr + {F} + F.Repr; }
  ///
  /// For every method, add:
  ///    requires Valid();
  ///    modifies Repr;
  ///    ensures Valid() && fresh(Repr - old(Repr));
  /// At the end of the body of the method, add:
  ///    if (A != null) { Repr := Repr + {A}; }
  ///    if (F != null) { Repr := Repr + {F} + F.Repr; }
  /// </summary>
  public class AutoContractsRewriter : IRewriter
  {
    public void PreResolve(ModuleDefinition m) {
      foreach (var d in m.TopLevelDecls) {
        bool sayYes = true;
        if (d is ClassDecl && Attributes.ContainsBool(d.Attributes, "autocontracts", ref sayYes) && sayYes) {
          ProcessClassPreResolve((ClassDecl)d);
        }
      }
    }

    void ProcessClassPreResolve(ClassDecl cl) {
      // Add:  ghost var Repr: set<object>;
      // ...unless a field with that name is already present
      if (!cl.Members.Exists(member => member is Field && member.Name == "Repr")) {
        Type ty = new SetType(new ObjectType());
        cl.Members.Add(new Field(new AutoGeneratedToken(cl.tok), "Repr", true, ty, null));
      }

      foreach (var member in cl.Members) {
        bool sayYes = true;
        if (Attributes.ContainsBool(member.Attributes, "autocontracts", ref sayYes) && !sayYes) {
          // the user has excluded this member
          continue;
        }
        if (member.RefinementBase != null) {
          // member is inherited from a module where it was already processed
          continue;
        }
        Boogie.IToken tok = new AutoGeneratedToken(member.tok);
        if (member is Function && member.Name == "Valid" && !member.IsStatic) {
          var valid = (Function)member;
          // reads this;
          valid.Reads.Add(new FrameExpression(tok, new ThisExpr(tok), null));
          // reads Repr;
          valid.Reads.Add(new FrameExpression(tok, new FieldSelectExpr(tok, new ImplicitThisExpr(tok), "Repr"), null));
        } else if (member is Constructor) {
          var ctor = (Constructor)member;
          // modifies this;
          ctor.Mod.Expressions.Add(new FrameExpression(tok, new ImplicitThisExpr(tok), null));
          // ensures Valid();
          ctor.Ens.Insert(0, new MaybeFreeExpression(new FunctionCallExpr(tok, "Valid", new ImplicitThisExpr(tok), tok, new List<Expression>())));
          // ensures fresh(Repr - {this});
          var freshness = new FreshExpr(tok, new BinaryExpr(tok, BinaryExpr.Opcode.Sub,
            new FieldSelectExpr(tok, new ImplicitThisExpr(tok), "Repr"),
            new SetDisplayExpr(tok, new List<Expression>() { new ThisExpr(tok) })));
          ctor.Ens.Insert(1, new MaybeFreeExpression(freshness));
        } else if (member is Method && !member.IsStatic) {
          var m = (Method)member;
          // requires Valid();
          m.Req.Insert(0, new MaybeFreeExpression(new FunctionCallExpr(tok, "Valid", new ImplicitThisExpr(tok), tok, new List<Expression>())));
          // If this is a mutating method, we should also add a modifies clause and a postcondition, but we don't do that if it's
          // a simple query method.  However, we won't know if it's a simple query method until after resolution, so we'll add the
          // rest of the spec then.
        }
      }
    }

    public void PostResolve(ModuleDefinition m) {
      foreach (var d in m.TopLevelDecls) {
        bool sayYes = true;
        if (d is ClassDecl && Attributes.ContainsBool(d.Attributes, "autocontracts", ref sayYes) && sayYes) {
          ProcessClassPostResolve((ClassDecl)d);
        }
      }
    }

    void ProcessClassPostResolve(ClassDecl cl) {
      // Find all fields of a reference type, and make a note of whether or not the reference type has a Repr field.
      // Also, find the Repr field and the function Valid in class "cl"
      Field ReprField = null;
      Function Valid = null;
      var subobjects = new List<Tuple<Field, Field>>();
      foreach (var member in cl.Members) {
        var field = member as Field;
        if (field != null) {
          bool sayYes = true;
          if (field.Name == "Repr") {
            ReprField = field;
          } else if (Attributes.ContainsBool(field.Attributes, "autocontracts", ref sayYes) && !sayYes) {
            // ignore this field
          } else if (field.Type is ObjectType) {
            subobjects.Add(new Tuple<Field, Field>(field, null));
          } else if (field.Type.IsRefType) {
            var rcl = (ClassDecl)((UserDefinedType)field.Type).ResolvedClass;
            Field rRepr = null;
            foreach (var memb in rcl.Members) {
              var f = memb as Field;
              if (f != null && f.Name == "Repr") {
                rRepr = f;
                break;
              }
            }
            subobjects.Add(new Tuple<Field, Field>(field, rRepr));
          }
        } else if (member is Function && member.Name == "Valid" && !member.IsStatic) {
          var fn = (Function)member;
          if (fn.Formals.Count == 0 && fn.ResultType is BoolType) {
            Valid = fn;
          }
        }
      }
      Contract.Assert(ReprField != null);  // we expect there to be a "Repr" field, since we added one in PreResolve

      Boogie.IToken clTok = new AutoGeneratedToken(cl.tok);
      Type ty = new UserDefinedType(clTok, cl.Name, cl, new List<Type>());
      var self = new ThisExpr(clTok);
      self.Type = ty;
      var implicitSelf = new ImplicitThisExpr(clTok);
      implicitSelf.Type = ty;
      var Repr = new FieldSelectExpr(clTok, implicitSelf, "Repr");
      Repr.Field = ReprField;
      Repr.Type = ReprField.Type;
      var cNull = new LiteralExpr(clTok);
      cNull.Type = new ObjectType();

      foreach (var member in cl.Members) {
        bool sayYes = true;
        if (Attributes.ContainsBool(member.Attributes, "autocontracts", ref sayYes) && !sayYes) {
          continue;
        }
        Boogie.IToken tok = new AutoGeneratedToken(member.tok);
        if (member is Function && member.Name == "Valid" && !member.IsStatic) {
          var valid = (Function)member;
          if (valid.IsGhost && valid.ResultType is BoolType) {
            Expression c;
            if (valid.RefinementBase == null) {
              var c0 = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.InSet, self, Repr);  // this in Repr
              var c1 = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.NotInSet, cNull, Repr);  // null !in Repr
              c = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.And, c0, c1);
            } else {
              c = new LiteralExpr(tok, true);
              c.Type = Type.Bool;
            }

            foreach (var ff in subobjects) {
              if (ff.Item1.RefinementBase != null) {
                // the field has been inherited from a refined module, so don't include it here
                continue;
              }
              var F = Resolver.NewFieldSelectExpr(tok, implicitSelf, ff.Item1, null);
              var c0 = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.NeqCommon, F, cNull);
              var c1 = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.InSet, F, Repr);
              if (ff.Item2 == null) {
                // F != null ==> F in Repr  (so, nothing else to do)
              } else {
                // F != null ==> F in Repr && F.Repr <= Repr && this !in F.Repr
                var FRepr = new FieldSelectExpr(tok, F, ff.Item2.Name);
                FRepr.Field = ff.Item2;
                FRepr.Type = ff.Item2.Type;
                var c2 = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.Subset, FRepr, Repr);
                var c3 = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.NotInSet, self, FRepr);
                c1 = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.And, c1, BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.And, c2, c3));
              }
              c = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.And, c, BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.Imp, c0, c1));
            }

            if (valid.Body == null) {
              valid.Body = c;
            } else {
              valid.Body = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.And, c, valid.Body);
            }
          }

        } else if (member is Constructor) {
          var ctor = (Constructor)member;
          if (ctor.Body != null) {
            var bodyStatements = ((BlockStmt)ctor.Body).Body;
            // Repr := {this};
            var e = new SetDisplayExpr(tok, new List<Expression>() { self });
            e.Type = new SetType(new ObjectType());
            Statement s = new AssignStmt(tok, Repr, new ExprRhs(e));
            s.IsGhost = true;
            bodyStatements.Add(s);

            AddSubobjectReprs(tok, subobjects, bodyStatements, self, implicitSelf, cNull, Repr);
          }

        } else if (member is Method && !member.IsStatic) {
          var m = (Method)member;
          if (Valid != null && !IsSimpleQueryMethod(m)) {
            if (member.RefinementBase == null) {
              // modifies Repr;
              m.Mod.Expressions.Add(new FrameExpression(Repr.tok, Repr, null));
              // ensures Valid();
              var valid = new FunctionCallExpr(tok, "Valid", implicitSelf, tok, new List<Expression>());
              valid.Function = Valid;
              valid.Type = Type.Bool;
              valid.TypeArgumentSubstitutions = new Dictionary<TypeParameter, Type>();
              m.Ens.Insert(0, new MaybeFreeExpression(valid));
              // ensures fresh(Repr - old(Repr));
              var e0 = new OldExpr(tok, Repr);
              e0.Type = Repr.Type;
              var e1 = new BinaryExpr(tok, BinaryExpr.Opcode.Sub, Repr, e0);
              e1.ResolvedOp = BinaryExpr.ResolvedOpcode.SetDifference;
              e1.Type = Repr.Type;
              var freshness = new FreshExpr(tok, e1);
              freshness.Type = Type.Bool;
              m.Ens.Insert(1, new MaybeFreeExpression(freshness));
            }

            if (m.Body != null) {
              var bodyStatements = ((BlockStmt)m.Body).Body;
              AddSubobjectReprs(tok, subobjects, bodyStatements, self, implicitSelf, cNull, Repr);
            }
          }
        }
      }
    }

    void AddSubobjectReprs(Boogie.IToken tok, List<Tuple<Field, Field>> subobjects, List<Statement> bodyStatements,
      Expression self, Expression implicitSelf, Expression cNull, Expression Repr) {
      // TODO: these assignments should be included on every return path

      foreach (var ff in subobjects) {
        var F = Resolver.NewFieldSelectExpr(tok, implicitSelf, ff.Item1, null);  // create a resolved FieldSelectExpr
        Expression e = new SetDisplayExpr(tok, new List<Expression>() { F });
        e.Type = new SetType(new ObjectType());  // resolve here
        var rhs = new BinaryExpr(tok, BinaryExpr.Opcode.Add, Repr, e);
        rhs.ResolvedOp = BinaryExpr.ResolvedOpcode.Union;  // resolve here
        rhs.Type = Repr.Type;  // resolve here
        if (ff.Item2 == null) {
          // Repr := Repr + {F}  (so, nothing else to do)
        } else {
          // Repr := Repr + {F} + F.Repr
          var FRepr = Resolver.NewFieldSelectExpr(tok, F, ff.Item2, null);  // create resolved FieldSelectExpr
          rhs = new BinaryExpr(tok, BinaryExpr.Opcode.Add, rhs, FRepr);
          rhs.ResolvedOp = BinaryExpr.ResolvedOpcode.Union;  // resolve here
          rhs.Type = Repr.Type;  // resolve here
        }
        // Repr := Repr + ...;
        Statement s = new AssignStmt(tok, Repr, new ExprRhs(rhs));
        s.IsGhost = true;
        // wrap if statement around s
        e = BinBoolExpr(tok, BinaryExpr.ResolvedOpcode.NeqCommon, F, cNull);
        var thn = new BlockStmt(tok, new List<Statement>() { s });
        thn.IsGhost = true;
        s = new IfStmt(tok, e, thn, null);
        s.IsGhost = true;
        // finally, add s to the body
        bodyStatements.Add(s);
      }
    }

    bool IsSimpleQueryMethod(Method m) {
      // A simple query method has out parameters, its body has no effect other than to assign to them,
      // and the postcondition does not explicitly mention the pre-state.
      return m.Outs.Count != 0 && m.Body != null && LocalAssignsOnly(m.Body) &&
        m.Ens.TrueForAll(mfe => !MentionsOldState(mfe.E));
    }

    bool LocalAssignsOnly(Statement s) {
      Contract.Requires(s != null);
      if (s is AssignStmt) {
        var ss = (AssignStmt)s;
        return ss.Lhs.Resolved is IdentifierExpr;
      } else if (s is ConcreteUpdateStatement) {
        var ss = (ConcreteUpdateStatement)s;
        return ss.Lhss.TrueForAll(e => e.Resolved is IdentifierExpr);
      } else if (s is CallStmt) {
        return false;
      } else {
        foreach (var ss in s.SubStatements) {
          if (!LocalAssignsOnly(ss)) {
            return false;
          }
        }
      }
      return true;
    }

    /// <summary>
    /// Returns true iff 'expr' is a two-state expression, that is, if it mentions "old(...)" or "fresh(...)".
    /// </summary>
    static bool MentionsOldState(Expression expr) {
      Contract.Requires(expr != null);
      if (expr is OldExpr || expr is FreshExpr) {
        return true;
      }
      foreach (var ee in expr.SubExpressions) {
        if (MentionsOldState(ee)) {
          return true;
        }
      }
      return false;
    }

    public static BinaryExpr BinBoolExpr(Boogie.IToken tok, BinaryExpr.ResolvedOpcode rop, Expression e0, Expression e1) {
      var p = new BinaryExpr(tok, BinaryExpr.ResolvedOp2SyntacticOp(rop), e0, e1);
      p.ResolvedOp = rop;  // resolve here
      p.Type = Type.Bool;  // resolve here
      return p;
    }
  }


  /// <summary>
  /// For any function foo() with the :opaque attribute,
  /// hide the body, so that it can only be seen within its
  /// recursive clique (if any), or if the prgrammer
  /// specifically asks to see it via the reveal_foo() lemma
  /// </summary>
  public class OpaqueFunctionRewriter : IRewriter {
    //protected Dictionary<Function, Function> fullVersion;
    protected Dictionary<Function, Function> original;

    public void PreResolve(ModuleDefinition m) {
      //fullVersion = new Dictionary<Function, Function>();
      original = new Dictionary<Function, Function>();

      foreach (var d in m.TopLevelDecls) {
        if (d is ClassDecl) {
          DuplicateOpaqueClassFunctions((ClassDecl)d);
        }
      }
    }    

    public void PostResolve(ModuleDefinition m) {
      // Fix up the ensures clause of the full version of the function,
      // since it may refer to the original opaque function      
      foreach (var fn in ModuleDefinition.AllFunctions(m.TopLevelDecls)) {        
        if (isFullVersion(fn)) {  // Is this a function we created to supplement an opaque function?                  
          OpaqueFunctionVisitor visitor = new OpaqueFunctionVisitor();
          var context = new OpaqueFunctionContext(original[fn], fn);

          foreach (Expression ens in fn.Ens) {
            visitor.Visit(ens, context);
          }
        }
      }
    }

    // Is f the full version of an opaque function?
    protected bool isFullVersion(Function f) {
      return original.ContainsKey(f);
    }

    // Trims the body from the original function and then adds an internal,
    // full version, along with a lemma connecting the two
    protected void DuplicateOpaqueClassFunctions(ClassDecl c) {
      List<MemberDecl> newDecls = new List<MemberDecl>();
      foreach (MemberDecl member in c.Members) {
        if (member is Function) {
          var f = (Function)member;

          if (Attributes.Contains(f.Attributes, "opaque")) {
            // Create a copy, which will be the internal version with a full body
            // which will allow us to verify that the ensures are true
            var cloner = new Cloner();
            var fWithBody = cloner.CloneFunction(f, "#" + f.Name + "_FULL");  
            newDecls.Add(fWithBody);
            //fullVersion.Add(f, fWithBody);
            original.Add(fWithBody, f);

            var newToken = new Boogie.Token(f.tok.line, f.tok.col);
            newToken.filename = f.tok.filename;
            newToken._val = fWithBody.Name;
            newToken._kind = f.tok.kind;
            newToken._pos = f.tok.pos;
            fWithBody.tok = newToken;

            // Annotate the new function so we remember that we introduced it
            List<Attributes.Argument/*!*/>new_args = new List<Attributes.Argument/*!*/>();
            fWithBody.Attributes = new Attributes("opaque_full", new_args, fWithBody.Attributes);

            // Create a lemma to allow the user to selectively reveal the function's body          
            // That is, given:
            //   function {:opaque} foo(x:int, y:int) : int
            //     requires 0 <= x < 5;
            //     requires 0 <= y < 5;
            //     ensures foo(x, y) < 10;
            //   { x + y }
            // We produce:
            //   lemma reveal_foo()
            //     ensures forall x:int, y:int {:trigger foo(x,y)} :: 0 <= x < 5 && 0 <= y < 5 ==> foo(x,y) == foo_FULL(x,y);
            Expression reqExpr = new LiteralExpr(f.tok, true);
            foreach (Expression req in f.Req) {
              Expression newReq = cloner.CloneExpr(req);
              reqExpr = new BinaryExpr(f.tok, BinaryExpr.Opcode.And, reqExpr, newReq);
            }

            List<BoundVar> boundVars = new List<BoundVar>();
            foreach (Formal formal in f.Formals) {
              boundVars.Add(new BoundVar(f.tok, formal.Name, formal.Type));
            }

            // Build the implication connecting the function's requires to the connection with the revealed-body version
            Func<Function, IdentifierSequence> func_builder = func => new IdentifierSequence(new List<Bpl.IToken>() { func.tok }, func.tok, func.Formals.ConvertAll(x => (Expression)new IdentifierExpr(func.tok, x.Name))); 
            var oldEqualsNew = new BinaryExpr(f.tok, BinaryExpr.Opcode.Eq, func_builder(f), func_builder(fWithBody));
            var requiresImpliesOldEqualsNew = new BinaryExpr(f.tok, BinaryExpr.Opcode.Imp, reqExpr, oldEqualsNew);            

            MaybeFreeExpression newEnsures;
            if (f.Formals.Count > 0)
            {
              // Build an explicit trigger for the forall, so Z3 doesn't get confused
              Expression trigger = func_builder(f);
              List<Attributes.Argument/*!*/> args = new List<Attributes.Argument/*!*/>();
              Attributes.Argument/*!*/ anArg;
              anArg = new Attributes.Argument(f.tok, trigger);
              args.Add(anArg);
              Attributes attrs = new Attributes("trigger", args, null);

              newEnsures = new MaybeFreeExpression(new ForallExpr(f.tok, boundVars, null, requiresImpliesOldEqualsNew, attrs));
            }
            else
            {
              // No need for a forall
              newEnsures = new MaybeFreeExpression(oldEqualsNew);
            }
            var newEnsuresList = new List<MaybeFreeExpression>();
            newEnsuresList.Add(newEnsures);

            var reveal = new Method(f.tok, "reveal_" + f.Name, f.IsStatic, true, f.TypeArgs, new List<Formal>(), new List<Formal>(), new List<MaybeFreeExpression>(),
                                    new Specification<FrameExpression>(new List<FrameExpression>(), null), newEnsuresList,
                                    new Specification<Expression>(new List<Expression>(), null), null, null, false);
            newDecls.Add(reveal);

            // Update f's body to simply call the full version, so we preserve recursion checks, decreases clauses, etc.
            f.Body = func_builder(fWithBody);
          }
        }
      }
      c.Members.AddRange(newDecls);
    }


    protected class OpaqueFunctionContext {
      public Function original;   // The original declaration of the opaque function
      public Function full;       // The version we added that has a body

      public OpaqueFunctionContext(Function Orig, Function Full) {
        original = Orig;
        full = Full;
      }
    }

    class OpaqueFunctionVisitor : TopDownVisitor<OpaqueFunctionContext> {
      protected override bool VisitOneExpr(Expression expr, ref OpaqueFunctionContext context) {
        if (expr is FunctionCallExpr) {
          var e = (FunctionCallExpr)expr;

          if (e.Function == context.original) { // Attempting to call the original opaque function
            // Redirect the call to the full version
            e.Function = context.full;            
          }
        }
        return true;
      }
    }
  }
}
