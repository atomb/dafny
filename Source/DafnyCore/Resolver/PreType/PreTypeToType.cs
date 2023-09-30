//-----------------------------------------------------------------------------
//
// Copyright by the contributors to the Dafny Project
// SPDX-License-Identifier: MIT
//
//-----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Microsoft.Dafny;

/// <summary>
/// The purpose of the PreTypeToTypeVisitor is to fill in a type to each expression and other AST nodes where types make
/// sense. This computation of this type draws from two sources:
///    - the pre-type inferred earlier
///    - any user-supplied type
/// For most AST nodes, this will not consider subset types; instead, subset types are considered later during
/// the type adjustment phase.
///
/// Of the types filled in here, three special TypeProxy's are used.
///    - AdjustableType
///    - BottomTypePlaceholder
///    - ExactTypePlaceholder
/// </summary>
class PreTypeToTypeVisitor : ASTVisitor<IASTVisitorContext> {
  public override IASTVisitorContext GetContext(IASTVisitorContext astVisitorContext, bool inFunctionPostcondition) {
    return astVisitorContext;
  }

  private readonly SystemModuleManager systemModuleManager;

  public PreTypeToTypeVisitor(SystemModuleManager systemModuleManager) {
    this.systemModuleManager = systemModuleManager;
  }

  /// <summary>
  /// This method combines the inferred pre-type with any user-supplied type for newtype, subset type,
  /// and const declarations. Those are the declarations whose signature contains inferred elements.
  /// When the other declarations are visited, it is expected that the signatures of all top-level and
  /// member declarations have types. For example, to call NormalizeExpand() on a subset type requires
  /// knowing its Rhs type, and visiting a MemberSelectExpr for a constant field requires knowing the
  /// type of the field.
  /// </summary>
  public void VisitConstantsAndRedirectingTypes(List<TopLevelDecl> declarations) {
    foreach (var decl in declarations) {
      if (decl is NewtypeDecl newtypeDecl) {
        PreType2TypeUtil.Combine(newtypeDecl.BaseType, newtypeDecl.BasePreType, false);
      } else if (decl is SubsetTypeDecl subsetTypeDecl) {
        PreType2TypeUtil.Combine(subsetTypeDecl.Var.Type, subsetTypeDecl.Var.PreType, false);
      }
      if (decl is TopLevelDeclWithMembers topLevelDeclWithMembers) {
        foreach (var member in topLevelDeclWithMembers.Members.Where(member => member is ConstantField)) {
          var constField = (ConstantField)member;
          PreType2TypeUtil.Combine(constField.Type, constField.PreType, true);
        }
      }
    }
  }

  /// <summary>
  /// This method should be called only after VisitConstantsAndRedirectingTypes has been called.
  /// </summary>
  protected override void VisitOneDeclaration(TopLevelDecl decl) {
    if (decl is NewtypeDecl newtypeDecl) {
      PreType2TypeUtil.Combine(newtypeDecl.BaseType, newtypeDecl.BasePreType, false);
    } else if (decl is SubsetTypeDecl subsetTypeDecl) {
      PreType2TypeUtil.Combine(subsetTypeDecl.Var.Type, subsetTypeDecl.Var.PreType, false);
    }

    base.VisitOneDeclaration(decl);
  }

  public override void VisitField(Field field) {
    if (field is ConstantField ||
        (field.EnclosingClass is IteratorDecl iteratorDecl && iteratorDecl.DecreasesFields.Contains(field))) {
      // The type of the const might have been omitted in the program text and then inferred.
      // Also, the automatically generated _decreases fields of an iterator have inferred types.
      PreType2TypeUtil.Combine(field.Type, field.PreType, true);
    }

    base.VisitField(field);
  }

  private static void VisitVariableList(IEnumerable<IVariable> variables, bool allowFutureAdjustments) {
    foreach (var v in variables) {
      PreType2TypeUtil.Combine(v.Type, v.PreType, allowFutureAdjustments);
    }
  }

  protected override bool VisitOneExpression(Expression expr, IASTVisitorContext context) {
    if (expr is DatatypeUpdateExpr datatypeUpdateExpr) {
      // How a DatatypeUpdateExpr desugars depends on whether or not the expression is ghost, which hasn't been determined
      // yet. So, if there is a difference between the two, then pre-type resolution prepares two different resolved expressions.
      // The choice between these two is done in a later phase during resolution. For now, if there are two, we visit them both.
      // ASTVisitor arranges to visit ResolvedExpression, but we consider ResolvedCompiledExpression here.
      if (datatypeUpdateExpr.ResolvedCompiledExpression != datatypeUpdateExpr.ResolvedExpression) {
        VisitExpression(datatypeUpdateExpr.ResolvedCompiledExpression, context);
      }
    }
    return base.VisitOneExpression(expr, context);
  }

  protected override void PostVisitOneExpression(Expression expr, IASTVisitorContext context) {
    if (expr is LiteralExpr or ThisExpr) {
      // Note, for the LiteralExpr "null", we expect to get a possibly-null type, whereas for a reference-type ThisExpr, we expect
      // to get the non-null type. The .PreType of these two distinguish between those cases, because the latter has a .PrintablePreType
      // field that gives the non-null type.
      expr.Type = PreType2TypeUtil.PreType2FixedType(expr.PreType);
      return;
    } else if (expr is FunctionCallExpr functionCallExpr) {
      functionCallExpr.TypeApplication_AtEnclosingClass = functionCallExpr.PreTypeApplication_AtEnclosingClass.ConvertAll(PreType2TypeUtil.PreType2FixedType);
      functionCallExpr.TypeApplication_JustFunction = PreType2TypeUtil.Combine(functionCallExpr.TypeApplication_JustFunction,
        functionCallExpr.PreTypeApplication_JustFunction, true);
    } else if (expr is MemberSelectExpr memberSelectExpr) {
      memberSelectExpr.TypeApplication_AtEnclosingClass = memberSelectExpr.PreTypeApplication_AtEnclosingClass.ConvertAll(PreType2TypeUtil.PreType2FixedType);
      memberSelectExpr.TypeApplication_JustMember =
        PreType2TypeUtil.Combine(memberSelectExpr.TypeApplication_JustMember, memberSelectExpr.PreTypeApplication_JustMember, true);
    } else if (expr is ComprehensionExpr comprehensionExpr) {
      VisitVariableList(comprehensionExpr.BoundVars, false);
    } else if (expr is LetExpr letExpr) {
      VisitVariableList(letExpr.BoundVars, letExpr.Exact);
      foreach (var lhs in letExpr.LHSs) {
        VisitPattern(lhs, context);
      }
    } else if (expr is DatatypeValue datatypeValue) {
      Contract.Assert(datatypeValue.InferredTypeArgs.Count == 0 || datatypeValue.InferredTypeArgs.Count == datatypeValue.InferredPreTypeArgs.Count);
      if (datatypeValue.InferredTypeArgs.Count == 0) {
        var datatypeDecl = datatypeValue.Ctor.EnclosingDatatype;
        Contract.Assert(datatypeValue.InferredPreTypeArgs.Count == datatypeDecl.TypeArgs.Count);
        for (var i = 0; i < datatypeDecl.TypeArgs.Count; i++) {
          var formal = datatypeDecl.TypeArgs[i];
          var actualPreType = datatypeValue.InferredPreTypeArgs[i];
          datatypeValue.InferredTypeArgs.Add(PreType2TypeUtil.PreType2AdjustableType(actualPreType, formal.Variance));
        }
      }
    } else if (expr is ConversionExpr conversionExpr) {
      PreType2TypeUtil.Combine(conversionExpr.ToType, conversionExpr.PreType, false);
      expr.Type = conversionExpr.ToType;
      return;
    }

    if (expr.PreType is UnusedPreType) {
      expr.Type = new InferredTypeProxy();
    } else if (expr is ConcreteSyntaxExpression { ResolvedExpression: { } resolvedExpression }) {
      expr.UnnormalizedType = resolvedExpression.UnnormalizedType;
    } else if (expr is SeqSelectExpr { Seq: { Type: { AsMultiSetType: { } } } }) {
      expr.UnnormalizedType = systemModuleManager.Nat();
    } else {
      expr.UnnormalizedType = PreType2TypeUtil.PreType2AdjustableType(expr.PreType, TypeParameter.TPVariance.Co);
    }
    base.PostVisitOneExpression(expr, context);
  }

  private void VisitPattern<VT>(CasePattern<VT> casePattern, IASTVisitorContext context) where VT : class, IVariable {
    if (casePattern.Var != null) {
      PreType2TypeUtil.Combine(casePattern.Var.Type, casePattern.Var.PreType, false);
    }
    VisitExpression(casePattern.Expr, context);

    casePattern.Arguments?.ForEach(v => VisitPattern(v, context));
  }

  protected override bool VisitOneStatement(Statement stmt, IASTVisitorContext context) {
    if (stmt is VarDeclStmt varDeclStmt) {
      VisitVariableList(varDeclStmt.Locals, true);
    } else if (stmt is VarDeclPattern varDeclPattern) {
      VisitVariableList(varDeclPattern.LocalVars, true);
    } else if (stmt is ForLoopStmt forLoopStmt) {
      PreType2TypeUtil.Combine(forLoopStmt.LoopIndex.Type, forLoopStmt.LoopIndex.PreType, false);
    } else if (stmt is ForallStmt forallStmt) {
      VisitVariableList(forallStmt.BoundVars, false);
    }

    return base.VisitOneStatement(stmt, context);
  }

  protected override void PostVisitOneStatement(Statement stmt, IASTVisitorContext context) {
    if (stmt is VarDeclPattern varDeclPattern) {
      VisitPattern(varDeclPattern.LHS, context);
    } else if (stmt is AssignStmt { Rhs: TypeRhs tRhs }) {
      Type rhsType;
      // convert the type of the RHS, which we expect to be a reference type, and then create the non-null version of it
      var udtConvertedFromPretype = (UserDefinedType)PreType2TypeUtil.PreType2FixedType(tRhs.PreType);
      Contract.Assert(udtConvertedFromPretype.IsRefType);
      if (tRhs.ArrayDimensions != null) {
        // In this case, we expect tRhs.PreType (and udtConvertedFromPretype) to be an array type
        var arrayPreType = (DPreType)tRhs.PreType.Normalize();
        Contract.Assert(arrayPreType.Decl is ArrayClassDecl);
        Contract.Assert(arrayPreType.Arguments.Count == 1);
        Contract.Assert(udtConvertedFromPretype.ResolvedClass is ArrayClassDecl);
        Contract.Assert(udtConvertedFromPretype.TypeArgs.Count == 1);

        // The user-supplied tRhs.EType may have some components that are more exact than what's in udtConvertedFromPretype, since
        // tRhs.EType may contain user-supplied subset types. But tRhs.EType may also be missing some type arguments altogether, because
        // they may have been omitted in the source text. The following has the effect of filling in any such missing components with
        // whatever was inferred during pre-type inference.
        PreType2TypeUtil.Combine(tRhs.EType, arrayPreType.Arguments[0], false);
        var arrayTypeDecl = systemModuleManager.arrayTypeDecls[tRhs.ArrayDimensions.Count];
        var rhsMaybeNullType = new UserDefinedType(stmt.tok, arrayTypeDecl.Name, arrayTypeDecl, new List<Type>() { tRhs.EType });
        rhsType = UserDefinedType.CreateNonNullType(rhsMaybeNullType);
      } else {
        // Fill in any missing type arguments in the user-supplied tRhs.EType.
        PreType2TypeUtil.Combine(tRhs.EType, tRhs.PreType, false);
        rhsType = (UserDefinedType)tRhs.EType;
        if (tRhs.InitCall != null) {
          // We want the type of tRhs.InitCall.MethodSelect.Obj to be the same as what the "new" gives, but the previous
          // visitation of this MemberSelectExpr would have set it to the type obtained from the pre-type. Since the MemberSelectExpr
          // won't be visited again during type adjustment, we set it here once and for all.
          tRhs.InitCall.MethodSelect.Obj.UnnormalizedType = rhsType;
        }
      }
      tRhs.Type = rhsType;

    } else if (stmt is AssignSuchThatStmt assignSuchThatStmt) {
      foreach (var lhs in assignSuchThatStmt.Lhss) {
        VisitExpression(lhs, context);
      }

    } else if (stmt is ProduceStmt produceStmt) {
      if (produceStmt.HiddenUpdate != null) {
        VisitStatement(produceStmt.HiddenUpdate, context);
      }

    } else if (stmt is CalcStmt calcStmt) {
      // The expression in each line has been visited, but pairs of those lines are then put together to
      // form steps. These steps (are always boolean, and) need to be visited, too. Their subexpressions
      // have already been visited, so it suffices to call PostVisitOneExpression (instead of VisitExpression)
      // here.
      foreach (var step in calcStmt.Steps) {
        PostVisitOneExpression(step, context);
      }
      PostVisitOneExpression(calcStmt.Result, context);
    }

    base.PostVisitOneStatement(stmt, context);
  }

  protected override void VisitExtendedPattern(ExtendedPattern pattern, IASTVisitorContext context) {
    switch (pattern) {
      case DisjunctivePattern disjunctivePattern:
        break;
      case LitPattern litPattern:
        PostVisitOneExpression(litPattern.OptimisticallyDesugaredLit, context);
        break;
      case IdPattern idPattern:
        if (idPattern.BoundVar != null) {
          PreType2TypeUtil.Combine(idPattern.BoundVar.Type, idPattern.BoundVar.PreType, false);
        }
        if (idPattern.ResolvedLit != null) {
          PostVisitOneExpression(idPattern.ResolvedLit, context);
        }
        break;
      default:
        Contract.Assert(false); // unexpected case
        break;
    }
    base.VisitExtendedPattern(pattern, context);
  }

}
