﻿namespace SQLow
open System
open System.Collections.Generic
open SQLow.InferredTypes

type private TypeCheckerContext(typeInference : ITypeInferenceContext) =
    let comparer =
        { new IEqualityComparer<SchemaTable> with
            member __.Equals(t1, t2) = t1.TableName = t2.TableName && t1.SchemaName = t2.SchemaName
            member __.GetHashCode(t) = (t.TableName, t.SchemaName).GetHashCode()
        }
    let referenced = HashSet<SchemaTable>(comparer)
    let written = HashSet<SchemaTable>(comparer)
    member __.Reference(table : SchemaTable) = ignore <| referenced.Add(table)
    member __.Write(table : SchemaTable) = ignore <| written.Add(table)
    member __.References = referenced :> _ seq
    member __.AnonymousVariable() = typeInference.AnonymousVariable()
    member __.Variable(parameter) = typeInference.Variable(parameter)
    member __.Unify(left, right) = typeInference.Unify(left, right)
    member __.Unify(inferredType, coreType : CoreColumnType) =
        typeInference.Unify(inferredType, InferredType.Dependent(inferredType, coreType))
    member __.Unify(inferredType, resultType : Result<InferredType, string>) =
        match resultType with
        | Ok t -> typeInference.Unify(inferredType, t)
        | Error _ as e -> e
    member __.Unify(types : InferredType seq) =
        types
        |> Seq.fold
            (function | Ok s -> (fun t -> typeInference.Unify(s, t)) | Error _ as e -> (fun _ -> e))
            (Ok InferredType.Any)
    member __.Concrete(inferred) = typeInference.Concrete(inferred)

type private TypeChecker(cxt : TypeCheckerContext, scope : InferredSelectScope) =
    member this.ResolveTableInvocation(source : SourceInfo, tableInvocation : TableInvocation) =
        match tableInvocation.Arguments with
        | None ->
            foundAt source (scope.ResolveTableReference(tableInvocation.Table, cxt.Reference))
        | Some args ->
            failAt source "Table invocations with arguments are not supported"

    member this.InferScalarSubqueryType(subquery : SelectStmt) : InferredType =
        let queryType = this.InferQueryType(subquery)
        if queryType.Columns.Count = 1 then
            queryType.Columns.[0].InferredType
        else
            failAt subquery.Source <|
                sprintf "A scalar subquery must have exactly 1 result column (found %d columns)"
                    queryType.Columns.Count

    member this.InferSimilarityExprType(sim : SimilarityExpr) =
        result {
            let! inputType = cxt.Unify(this.InferExprType(sim.Input), StringType)
            let! patternType = cxt.Unify(this.InferExprType(sim.Pattern), StringType)
            match sim.Escape with
            | None -> ()
            | Some escape -> ignore <| cxt.Unify(this.InferExprType(escape), StringType)
            let! unified = cxt.Unify(inputType, patternType)
            return InferredType.Dependent(unified, BooleanType)
        }

    member this.InferInExprType(input, set : InSet WithSource) =
        let inputType = this.InferExprType(input)
        result {
            let! setType =
                match set.Value with
                | InExpressions exprs ->
                    let commonType = cxt.Unify(exprs |> Seq.map this.InferExprType)
                    cxt.Unify(inputType, commonType)
                | InSelect select ->
                    cxt.Unify(inputType, this.InferScalarSubqueryType(select))
                | InTable table ->
                    let query = this.ResolveTableInvocation(set.Source, table)
                    if query.Columns.Count <> 1 then
                        failAt set.Source <|
                            sprintf "Right side of IN operator must have exactly one column (this one has %d)"
                                query.Columns.Count
                    else cxt.Unify(inputType, query.Columns.[0].InferredType)
            return InferredType.Dependent(inputType, BooleanType)
        }

    member this.InferCaseExprType(cases : CaseExpr) =
        let mutable outputType = InferredType.Any
        match cases.Input with
        | None ->
            for condition, output in cases.Cases do
                this.RequireExprType(condition, BooleanType)
                outputType <- cxt.Unify(this.InferExprType(output), outputType) |> resultAt output.Source
        | Some input ->
            let mutable inputType = this.InferExprType(input)
            for input, output in cases.Cases do
                inputType <- cxt.Unify(this.InferExprType(input), inputType) |> resultAt input.Source
                outputType <- cxt.Unify(this.InferExprType(output), outputType) |> resultAt output.Source
        match cases.Else.Value with
        | None -> // require nullable
            cxt.Unify(outputType, ConcreteType { Nullable = true; Type = AnyType })
            |> resultAt cases.Else.Source
        | Some els ->
            cxt.Unify(this.InferExprType(els), outputType) |> resultAt els.Source

    member this.InferBinaryExprType({ Operator = op; Left = left; Right = right }) =
        let leftType, rightType = this.InferExprType(left), this.InferExprType(right)
        match op with
        | Concatenate -> cxt.Unify([ leftType; rightType; InferredType.String ])
        | Multiply
        | Divide
        | Add
        | Subtract -> cxt.Unify([ leftType; rightType; InferredType.Number ])
        | Modulo
        | BitShiftLeft
        | BitShiftRight
        | BitAnd
        | BitOr -> cxt.Unify([ leftType; rightType; InferredType.Integer ])
        | LessThan
        | LessThanOrEqual
        | GreaterThan
        | GreaterThanOrEqual
        | Equal
        | NotEqual
        | Is
        | IsNot ->
            result {
                let! operandType = cxt.Unify(leftType, rightType)
                return InferredType.Dependent(operandType, BooleanType)
            }
        | And
        | Or -> cxt.Unify([ leftType; rightType; InferredType.Boolean ])

    member this.InferUnaryExprType({ Operator = unop; Operand = operand }) =
        let operandType = this.InferExprType(operand)
        match unop with
        | Negative
        | BitNot -> cxt.Unify(operandType, InferredType.Number)
        | Not -> cxt.Unify(operandType, InferredType.Boolean)
        | IsNull
        | NotNull -> result { return InferredType.Boolean }

    member this.InferFunctionType(source, func : FunctionInvocationExpr) =
        match scope.Model.Builtin.Functions.TryFind(func.FunctionName) with
        | None -> failAt source <| sprintf "No such function: ``%O``" func.FunctionName
        | Some funcType ->
            let functionVars = Dictionary()
            let toInferred (ty : ArgumentType) =
                match ty with
                | ArgumentConcrete t -> ConcreteType t
                | ArgumentTypeVariable name ->
                    let succ, tvar = functionVars.TryGetValue(name)
                    if succ then tvar else
                    let avar = cxt.AnonymousVariable()
                    functionVars.[name] <- avar
                    avar
            match func.Arguments with
            | ArgumentWildcard ->
                if funcType.AllowWildcard then toInferred funcType.Output
                else failAt source <| sprintf "Function does not permit wildcards: ``%O``" func.FunctionName
            | ArgumentList (distinct, args) ->
                if Option.isSome distinct && not funcType.AllowDistinct then
                    failAt source <| sprintf "Function does not permit DISTINCT keyword: ``%O``" func.FunctionName
                else
                    let mutable lastIndex = 0
                    for i, expectedTy in funcType.FixedArguments |> Seq.indexed do
                        if i >= args.Count then
                            failAt source <|
                                sprintf "Function %O expects at least %d arguments but given only %d"
                                    func.FunctionName
                                    funcType.FixedArguments.Count
                                    args.Count
                        else
                            let argTy = this.InferExprType(args.[i])
                            ignore <| (cxt.Unify(toInferred expectedTy, argTy) |> resultAt args.[i].Source)
                        lastIndex <- i
                    for i = lastIndex + 1 to args.Count - 1 do
                        match funcType.VariableArgument with
                        | None ->
                            failAt args.[i].Source <|
                                sprintf "Function %O does not accept more than %d arguments"
                                    func.FunctionName
                                    funcType.FixedArguments.Count
                        | Some varArg ->
                            let varArg = toInferred varArg
                            ignore <| (cxt.Unify(this.InferExprType(args.[i]), varArg) |> resultAt args.[i].Source)
                    toInferred funcType.Output

    member this.RequireExprType(expr : Expr, mustMatch : CoreColumnType) =
        let inferred = this.InferExprType(expr)
        cxt.Unify(inferred, mustMatch)
        |> resultAt expr.Source
        |> ignore

    member this.InferExprType(expr : Expr) : InferredType =
        match expr.Value with
        | LiteralExpr lit -> InferredType.OfLiteral(lit)
        | BindParameterExpr par -> cxt.Variable(par)
        | ColumnNameExpr name ->
            let column = scope.ResolveColumnReference(name) |> foundAt expr.Source
            column.InferredType
        | CastExpr cast -> InferredType.OfTypeName(cast.AsType, this.InferExprType(cast.Expression))
        | CollateExpr (subExpr, collation) ->
            let inferred = this.InferExprType(subExpr)
            cxt.Unify(inferred, InferredType.String)
            |> resultAt expr.Source
        | FunctionInvocationExpr funcInvoke -> this.InferFunctionType(expr.Source, funcInvoke)
        | NotSimilarityExpr sim
        | SimilarityExpr sim ->
            this.InferSimilarityExprType(sim)
            |> resultAt expr.Source
        | BinaryExpr bin ->
            this.InferBinaryExprType(bin)
            |> resultAt expr.Source
        | UnaryExpr un ->
            this.InferUnaryExprType(un)
            |> resultAt expr.Source
        | BetweenExpr { Input = input; Low = low; High = high }
        | NotBetweenExpr { Input = input; Low = low; High = high } ->
            result {
                let! unified = cxt.Unify([ input; low; high ] |> Seq.map this.InferExprType)
                return InferredType.Dependent(unified, BooleanType)
            } |> resultAt expr.Source
        | InExpr (input, set)
        | NotInExpr (input, set) ->
            this.InferInExprType(input, set)
            |> resultAt expr.Source
        | ExistsExpr select ->
            ignore <| this.InferQueryType(select)
            InferredType.Boolean
        | CaseExpr cases -> this.InferCaseExprType(cases)
        | ScalarSubqueryExpr subquery -> this.InferScalarSubqueryType(subquery)
        | RaiseExpr _ -> InferredType.Any

    member this.CTEScope(withClause : WithClause option) =
        match withClause with
        | None -> scope
        | Some ctes ->
            ctes.Tables |> Seq.fold (fun scope cte ->
                let cteQuery =
                    match cte.ColumnNames with
                    | None -> this.InferQueryType(cte.AsSelect)
                    | Some names ->
                        this.InferQueryType(cte.AsSelect).RenameColumns(names.Value)
                        |> resultAt names.Source
                { scope with
                    CTEVariables = scope.CTEVariables |> Map.add cte.Name cteQuery
                }) scope

    member private this.TableExprScope(dict : Dictionary<Name, _>, tableExpr : TableExpr) =
        let add name query =
            if dict.ContainsKey(name) then
                failAt tableExpr.Source <| sprintf "Table name already in scope: ``%O``" name
            else
                dict.Add(name, query)
        match tableExpr.Value with
        | TableOrSubquery (Table (invoc, alias, indexHint)) ->
            let tbl = this.ResolveTableInvocation(tableExpr.Source, invoc)
            let name = defaultArg alias invoc.Table.ObjectName
            add name tbl
            tbl
        | TableOrSubquery (Subquery (select, alias)) ->
            let sub = this.InferQueryType(select)
            match alias with
            | Some alias -> add alias sub
            | None -> ()
            sub
        | Join join ->
            let left = this.TableExprScope(dict, join.LeftTable)
            let right = this.TableExprScope(dict, join.RightTable)
            left.Append(right)

    member this.TableExprScope(tableExpr : TableExpr) =
        let dict = Dictionary()
        let wildcard = this.TableExprScope(dict, tableExpr)
        { FromVariables = dict; Wildcard = wildcard }

    member this.ValidateTableExprConstraints(tableExpr : TableExpr) =
        match tableExpr.Value with
        | TableOrSubquery _ -> ()
        | Join join ->
            this.ValidateTableExprConstraints(join.LeftTable)
            this.ValidateTableExprConstraints(join.RightTable)
            match join.JoinType, join.Constraint with
            | Natural _, JoinUnconstrained ->
                let columnSet texpr = 
                    this.TableExprScope(texpr).Wildcard.Columns
                    |> Seq.map (fun c -> c.ColumnName)
                    |> Set.ofSeq
                let intersection = Set.intersect (columnSet join.LeftTable) (columnSet join.RightTable)
                if Set.isEmpty intersection then
                    failAt tableExpr.Source
                        "The left and right sides of a NATURAL JOIN must have at least one column name in common"
            | Natural _, _ -> failAt tableExpr.Source "A NATURAL JOIN cannot have an ON or USING clause"
            | _, JoinOn ex -> this.RequireExprType(ex, BooleanType)
            | _, JoinUsing names ->
                let leftColumns = this.TableExprScope(join.LeftTable).Wildcard
                let rightColumns = this.TableExprScope(join.RightTable).Wildcard
                for name in names do
                    leftColumns.ColumnByName(name) |> foundAt tableExpr.Source |> ignore
                    rightColumns.ColumnByName(name) |> foundAt tableExpr.Source |> ignore
            | _, JoinUnconstrained -> ()

    member this.InferSelectCoreType(select : SelectCore) : InferredQuery =
        let this, scope =
            match select.From with
            | None -> this, scope
            | Some tableExpr ->
                let fromScope = { scope with FromClause = this.TableExprScope(tableExpr) |> Some }
                let fromInferrer = TypeChecker(cxt, fromScope)
                fromInferrer.ValidateTableExprConstraints(tableExpr)
                fromInferrer, fromScope
        match select.Where with
        | None -> ()
        | Some whereExpr -> this.RequireExprType(whereExpr, BooleanType)
        // TODO: we need to figure out what to do about aggregates.
        // How can we cleanly prevent bogus queries like `select *, count(*) from tbl`?
        match select.GroupBy with
        | None -> ()
        | Some groupBy ->
            for expr in groupBy.By do this.RequireExprType(expr, AnyType)
            match groupBy.Having with
            | None -> ()
            | Some havingExpr -> this.RequireExprType(havingExpr, BooleanType)
        let resultColumns =
            seq {
              for col in select.Columns.Columns do
                match col.Value with
                | ColumnsWildcard ->
                    match scope.FromClause with
                    | None -> failAt col.Source "Can't use wildcard without a FROM clause"
                    | Some from -> yield! from.Wildcard.Columns
                | TableColumnsWildcard objectName ->
                    match scope.FromClause with
                    | None ->
                        failAt col.Source <|
                            sprintf "Can't use wildcard ``%O.*`` without a FROM clause" objectName
                    | Some from ->
                        let table = from.ResolveTable(objectName) |> foundAt col.Source
                        yield! table.Columns
                | Column (expr, alias) ->
                    let inferred, pk = 
                        match expr.Value with
                        | ColumnNameExpr name ->
                            let column = scope.ResolveColumnReference(name) |> foundAt expr.Source
                            column.InferredType, column.PrimaryKey
                        | _ -> this.InferExprType(expr), false
                    let name, fromAlias =
                        match alias with
                        | None ->
                            match expr.Value with
                            | ColumnNameExpr name -> name.ColumnName, name.Table |> Option.map (fun t -> t.ObjectName)
                            | _ -> failAt expr.Source "An expression-valued column must have an alias"
                        | Some name -> name, None
                    yield
                        { ColumnName = name; InferredType = inferred; FromAlias = fromAlias; PrimaryKey = pk }
            } |> toReadOnlyList
        {
            Columns = resultColumns
        }

    member this.InferCompoundTermType(compound : CompoundTerm) : InferredQuery =
        match compound.Value with
        | Values rows ->
            if rows.Count < 1 then failAt compound.Source "VALUES clause must contain at least one row"
            let rowType =
                [| for col in rows.[0].Value -> this.InferExprType(col) |]
            for row in rows |> Seq.skip 1 do
                if row.Value.Count <> rowType.Length then
                    failAt row.Source <|
                        sprintf "Row in VALUES clause has too %s values (expected %d, got %d)"
                            (if row.Value.Count > rowType.Length then "many" else "few")
                            rowType.Length row.Value.Count
                for i, col in row.Value |> Seq.indexed do
                    let inferred = this.InferExprType(col)
                    rowType.[i] <- cxt.Unify(inferred, rowType.[i]) |> resultAt col.Source
            { Columns =
                [| for inferred in rowType ->
                    { InferredType = inferred; FromAlias = None; ColumnName = Name(""); PrimaryKey = false } |]
            }
        | Select core -> this.InferSelectCoreType(core)

    member this.InferCompoundExprType(compound : CompoundExpr) : InferredQuery =
        match compound.Value with
        | CompoundTerm term -> this.InferCompoundTermType(term)
        | Union (top, bottom)
        | UnionAll (top, bottom)
        | Intersect (top, bottom)
        | Except (top, bottom) ->
            let topType = this.InferCompoundExprType(top)
            let bottomType = this.InferCompoundTermType(bottom)
            if topType.Columns.Count <> bottomType.Columns.Count then
                failAt bottom.Source <|
                    sprintf "Mismatched number of columns in compound expression (%d on top, %d on bottom)"
                        topType.Columns.Count bottomType.Columns.Count
            else
                { topType with
                    Columns =
                        [| for topCol, botCol in Seq.zip topType.Columns bottomType.Columns ->
                            { topCol with
                                InferredType =
                                    cxt.Unify(topCol.InferredType, botCol.InferredType)
                                    |> resultAt bottom.Source
                            }
                        |]
                }
    member this.InferQueryType(select : SelectStmt) : InferredQuery =
        let innerScope = this.CTEScope(select.Value.With)
        let inner = TypeChecker(cxt, scope)
        let compoundExprType = inner.InferCompoundExprType(select.Value.Compound)
        // At this point we know the expression type, we just need to validate the
        // other optional bits of the query.
        match select.Value.OrderBy with
        | None -> ()
        | Some orderTerms ->
            // TODO: We should check that only selected expressions are ordered by.
            for term in orderTerms do inner.RequireExprType(term.By, AnyType)
        match select.Value.Limit with
        | None -> ()
        | Some limit -> // Limit expressions can use the current scope, not the inner scope.
            this.RequireExprType(limit.Limit, IntegerType)
            match limit.Offset with
            | None -> ()
            | Some offset -> this.RequireExprType(offset, IntegerType)
        compoundExprType
