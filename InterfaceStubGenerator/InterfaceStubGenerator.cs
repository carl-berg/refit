﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nustache;
using Nustache.Core;

namespace Refit.Generator
{
    // * Search for all Interfaces, find the method definitions 
    //   and make sure there's at least one Refit attribute on one
    // * Generate the data we need for the template based on interface method
    //   defn's
    // * Get this into an EXE in tools, write a targets file to beforeBuild execute it
    // * Get a props file that adds a dummy file to the project
    // * Write an implementation of RestService that just takes the interface name to
    //   guess the class name based on our template
    //
    // What if the Interface is in another module? (since we copy usings, should be fine)
    public class InterfaceStubGenerator
    {
        public string GenerateInterfaceStubs(string[] paths)
        {
            var trees = paths.Select(x => CSharpSyntaxTree.ParseFile(x)).ToList();

            var interfacesToGenerate = trees.SelectMany(FindInterfacesToGenerate).ToList();

            var templateInfo = GenerateTemplateInfoForInterfaceList(interfacesToGenerate);

            Encoders.HtmlEncode = (s) => s;
            var text = Render.StringToString(ExtractTemplateSource(), templateInfo);
            return text;
        }

        public List<InterfaceDeclarationSyntax> FindInterfacesToGenerate(SyntaxTree tree)
        {
            var nodes = tree.GetRoot().DescendantNodes().ToList();

            // Make sure this file imports Refit. If not, we're not going to 
            // find any Refit interfaces
            // NB: This falls down in the tests unless we add an explicit "using Refit;",
            // but we can rely on this being there in any other file
            if (nodes.OfType<UsingDirectiveSyntax>().All(u => u.Name.ToFullString() != "Refit"))
                return new List<InterfaceDeclarationSyntax>();

            return nodes.OfType<InterfaceDeclarationSyntax>()
                .Where(i => i.Members.OfType<MethodDeclarationSyntax>().Any(HasRefitHttpMethodAttribute))
                .ToList();
        }

        static readonly HashSet<string> httpMethodAttributeNames = new HashSet<string>(
            new[] {"Get", "Head", "Post", "Put", "Delete"}
                .SelectMany(x => new[] {"{0}", "{0}Attribute"}.Select(f => string.Format(f, x))));

        public bool HasRefitHttpMethodAttribute(MethodDeclarationSyntax method)
        {
            // We could also verify that the single argument is a string, 
            // but what if somebody is dumb and uses a constant?
            // Could be turtles all the way down.
            return method.AttributeLists.SelectMany(a => a.Attributes)
                .Any(a => httpMethodAttributeNames.Contains(a.Name.ToFullString().Split('.').Last()) &&
                    a.ArgumentList.Arguments.Count == 1 &&
                    a.ArgumentList.Arguments[0].Expression.CSharpKind() == SyntaxKind.StringLiteralExpression);
        }

        public TemplateInformation GenerateTemplateInfoForInterfaceList(List<InterfaceDeclarationSyntax> interfaceList)
        {
            var usings = interfaceList
                .SelectMany(interfaceTree => {
                    var rootNode = interfaceTree.Parent;
                    while (rootNode.Parent != null) rootNode = rootNode.Parent;

                    return rootNode.DescendantNodes()
                        .OfType<UsingDirectiveSyntax>()
                        .Select(x => string.Format("{0} {1}", x.Alias, x.Name).TrimStart());
                })
                .Distinct()
                .Where(x => x != "System" && x != "System.Net.Http" && x != "System.Collections.Generic" && x != "System.Linq")
                .Select(x => new UsingDeclaration() { Item = x });

            var ret = new TemplateInformation() {
                ClassList = interfaceList.Select(x => GenerateClassInfoForInterface(x)).ToList(),
                UsingList = usings.ToList(),
            };

            return ret;
        }

        public ClassTemplateInfo GenerateClassInfoForInterface(InterfaceDeclarationSyntax interfaceTree)
        {
            var ret = new ClassTemplateInfo();
            var parent = interfaceTree.Parent;
            while (parent != null && !(parent is NamespaceDeclarationSyntax)) parent = parent.Parent;

            var ns = parent as NamespaceDeclarationSyntax;
            ret.Namespace = ns.Name.ToString();
            ret.InterfaceName = interfaceTree.Identifier.ValueText;

            if (interfaceTree.TypeParameterList != null) {
                var typeParameters = interfaceTree.TypeParameterList.Parameters;
                if (typeParameters.Any()) {
                    ret.TypeParameters = string.Join(", ", typeParameters.Select(p => p.Identifier.ValueText));
                }
                ret.ConstraintClauses = interfaceTree.ConstraintClauses.ToFullString().Trim();
            }
            ret.MethodList = interfaceTree.Members
                .OfType<MethodDeclarationSyntax>()
                .Select(x => new MethodTemplateInfo() {
                    Name = x.Identifier.ValueText,
                    ReturnType = x.ReturnType.ToString(),
                    ArgumentList = String.Join(",", x.ParameterList.Parameters
                        .Select(y => y.Identifier.ValueText)),
                    ArgumentListWithTypes = String.Join(",", x.ParameterList.Parameters
                        .Select(y => String.Format("{0} {1}", y.Type.ToString(), y.Identifier.ValueText))),
                    IsRefitMethod = HasRefitHttpMethodAttribute(x)
                })
                .ToList();

            return ret;
        }

        public static string ExtractTemplateSource()
        {
            var ourPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Try to return a flat file from the same directory, if it doesn't
            // exist, use the built-in resource version
            if (File.Exists(ourPath)) {
                return File.ReadAllText(Path.Combine(ourPath, "GeneratedInterfaceStubTemplate.cs.mustache"), Encoding.UTF8);
            }

            using (var src = typeof(InterfaceStubGenerator).Assembly.GetManifestResourceStream("Refit.Generator.GeneratedInterfaceStubTemplate.mustache")) {
                var ms = new MemoryStream();
                src.CopyTo(ms);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    public class UsingDeclaration
    {
        public string Item { get; set; }
    }

    public class ClassTemplateInfo
    {
        public string Namespace { get; set; }
        public string InterfaceName { get; set; }
        public string TypeParameters { get; set; }
        public string ConstraintClauses { get; set; }
        public List<MethodTemplateInfo> MethodList { get; set; }
    }

    public class MethodTemplateInfo
    {
        public string ReturnType { get; set; }
        public string Name { get; set; }
        public string ArgumentListWithTypes { get; set; }
        public string ArgumentList { get; set; }
        public bool IsRefitMethod { get; set; }
    }

    public class TemplateInformation
    {
        public List<UsingDeclaration> UsingList { get; set; }
        public List<ClassTemplateInfo> ClassList;
    }
}