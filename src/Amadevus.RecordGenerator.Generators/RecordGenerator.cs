﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeGeneration.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Amadevus.RecordGenerator.Generators
{
    public class RecordGenerator : ICodeGenerator
    {
        private readonly AttributeData attributeData;

        private static readonly ImmutableArray<IPartialGenerator> PartialGenerators =
            ImmutableArray.Create(RecordPartialGenerator.Instance,
                                  BuilderPartialGenerator.Instance,
                                  DeconstructPartialGenerator.Instance,
                                  EqualityPartialGenerator.ObjectEqualsGenerator,
                                  EqualityPartialGenerator.EquatableEqualsPartialGenerator,
                                  EqualityPartialGenerator.OperatorEqualityPartialGenerator);

        public RecordGenerator(AttributeData attributeData)
        {
            this.attributeData = attributeData;
        }

        static readonly Task<SyntaxList<MemberDeclarationSyntax>> EmptyResultTask =
            Task.FromResult(List<MemberDeclarationSyntax>());

        public Task<SyntaxList<MemberDeclarationSyntax>> GenerateAsync(TransformationContext context, IProgress<Diagnostic> progress, CancellationToken cancellationToken)
        {
            return
                context.ProcessingNode is TypeDeclarationSyntax tds
                && (tds.IsKind(SyntaxKind.ClassDeclaration) || tds.IsKind(SyntaxKind.StructDeclaration))
                    ? GenerateAsync(tds)
                    : EmptyResultTask;

            Task<SyntaxList<MemberDeclarationSyntax>> GenerateAsync(TypeDeclarationSyntax typeDeclaration)
            {
                var descriptor = typeDeclaration.ToRecordDescriptor(context.SemanticModel);
                if (descriptor.Entries.IsEmpty)
                    return EmptyResultTask;

                var generatedMembers = new List<MemberDeclarationSyntax>();
                var features = GetFeatures();
                var partialKeyword = Token(SyntaxKind.PartialKeyword);

                var declaration = typeDeclaration.Kind() == SyntaxKind.ClassDeclaration
                        ? (TypeDeclarationSyntax)ClassDeclaration(descriptor.TypeIdentifier)
                        : (TypeDeclarationSyntax)StructDeclaration(descriptor.TypeIdentifier);

                var declarationWithTypeList = declaration.WithTypeParameterList(typeDeclaration.TypeParameterList?.WithoutTrivia());

                var partials =
                    from g in PartialGenerators
                    select g.Generate(descriptor, features)
                    into g
                    where !g.IsEmpty
                    select new
                    {
                        Declaration =
                            g.ContainsDiagnosticsOnly
                            ? null
                            : declarationWithTypeList
                              .WithBaseList(g.BaseTypes.IsEmpty ? null : BaseList(SeparatedList(g.BaseTypes)))
                              .WithModifiers(
                                  TokenList(
                                      g.Modifiers
                                      .Except(new[] { partialKeyword })
                                      .Append(partialKeyword)))
                              .WithMembers(List(g.Members))
                              .AddGeneratedCodeAttributeOnMembers(),
                        g.Diagnostics
                    };

                foreach (var partial in partials)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (partial.Declaration != null)
                        generatedMembers.Add(partial.Declaration);

                    foreach (var diagnostic in partial.Diagnostics)
                        progress.Report(diagnostic);
                }

                return generatedMembers.Count > 0
                     ? Task.FromResult(List(generatedMembers))
                     : EmptyResultTask;
            }
        }

        private Features GetFeatures()
        {
            return attributeData.ConstructorArguments.Length > 0
                ? (Features)attributeData.ConstructorArguments[0].Value
                : Features.Default;
        }
    }
}
