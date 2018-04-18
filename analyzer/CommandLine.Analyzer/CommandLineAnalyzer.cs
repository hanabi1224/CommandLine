using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CommandLine.Analyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public partial class CommandLineAnalyzer : DiagnosticAnalyzer
    {
        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSymbolAction(AnalyzeCommandLineType, SymbolKind.NamedType);
        }

        private static void AnalyzeCommandLineType(SymbolAnalysisContext context)
        {
            // first of all, nothing to do if we don't have an attribute in the Commandline namespace on the properties
            var namedTypeSymbol = context.Symbol as INamedTypeSymbol;

            if (namedTypeSymbol == null || !IsCommandLineArgumentClass(namedTypeSymbol))
            {
                return;
            }

            Dictionary<string, List<Argument>> mapGroupAndProps = CreateArgumentMapForType(namedTypeSymbol, context, out ActionArgument actionArg);

            // if an action argument has been specified but no arguments have been added to specific groups, give a warning
            if (actionArg != null && mapGroupAndProps.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(ActionWithoutArgumentsInGroup, actionArg.Symbol.Locations.First()));
            }

            ValidateDefinedProperties(mapGroupAndProps, actionArg, context);
        }

        private static Dictionary<string, List<Argument>> CreateArgumentMapForType(INamedTypeSymbol namedTypeSymbol, SymbolAnalysisContext context, out ActionArgument actionArg)
        {
            Dictionary<string, List<Argument>> mapGroupAndProps
                            = new Dictionary<string, List<Argument>>(StringComparer.OrdinalIgnoreCase);

            // find the action arg.
            var typeMembers = namedTypeSymbol.GetMembers();
            actionArg = GetActionArgument(typeMembers, context);

            // setup the groups available based on the actionArg
            if (actionArg == null)
            {
                mapGroupAndProps.Add("", new List<Argument>());
            }
            else
            {
                foreach (var grp in actionArg.Values)
                {
                    mapGroupAndProps.Add(grp, new List<Argument>());
                }
            }

            // traverse the properties again and add them to the groups as needed.
            foreach (var member in typeMembers)
            {
                var attributes = member.GetAttributes();
                if (!attributes.Any())
                {
                    continue;
                }

                // we should skip over the action argument.
                if (actionArg != null && actionArg.Symbol == member)
                    continue;

                Argument arg = null;
                List<string> argGroup = new List<string>();
                bool isCommon = false;
                bool isAttributeGroup = false;

                foreach (var attribute in attributes)
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals("CommandLine", attribute.AttributeClass.ContainingAssembly.Name))
                    {
                        continue;
                    }

                    if (attribute.AttributeClass.Name == "RequiredArgumentAttribute" && attribute.ConstructorArguments.Length >= 3)
                    {
                        RequiredArgument ra = new RequiredArgument();
                        ra.Position = (int)attribute.ConstructorArguments[0].Value; // position

                        ra.Name = attribute.ConstructorArguments[1].Value as string;
                        ra.Description = attribute.ConstructorArguments[2].Value as string;
                        if (attribute.ConstructorArguments.Length == 4)
                        {
                            ra.IsCollection = (bool)attribute.ConstructorArguments[3].Value;
                        }

                        if (arg != null)
                        {
                            // can't have a property be both optional and required
                            context.ReportDiagnostic(Diagnostic.Create(ConflictingPropertyDeclarationRule, member.Locations.First()));
                        }

                        arg = ra;
                    }
                    else if (attribute.AttributeClass.Name == "OptionalArgumentAttribute" && attribute.ConstructorArguments.Length >= 3)
                    {
                        OptionalArgument oa = new OptionalArgument();
                        oa.DefaultValue = attribute.ConstructorArguments[0].Value; // default value
                        oa.Name = attribute.ConstructorArguments[1].Value as string;
                        oa.Description = attribute.ConstructorArguments[2].Value as string;
                        if (attribute.ConstructorArguments.Length == 4)
                        {
                            oa.IsCollection = (bool)attribute.ConstructorArguments[3].Value;
                        }

                        if (arg != null)
                        {
                            // can't have a property be both optional and required
                            context.ReportDiagnostic(Diagnostic.Create(ConflictingPropertyDeclarationRule, member.Locations.First()));
                        }
                        arg = oa;
                    }

                    if (attribute.AttributeClass.Name == "CommonArgumentAttribute")
                    {
                        isCommon = true;
                    }

                    if (attribute.AttributeClass.Name == "ArgumentGroupAttribute")
                    {
                        isAttributeGroup = true;
                        argGroup.Add(attribute.ConstructorArguments[0].Value as string);
                    }
                }

                // we did not find the Required/Optional attribute on that type.
                if (arg == null)
                {
                    // we could not identify an argument because we don't have the required/optional attribute, but we do have the commonattribute/attributeGroup which does not make sense.
                    if (isCommon == true || isAttributeGroup == true)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(CannotSpecifyAGroupForANonPropertyRule, member.Locations.First()));
                    }

                    // we don't understand this argument, nothing to do.
                    continue;
                }

                // store the member symbol on the argument object
                arg.Symbol = member;

                // add the argument to all the groups
                if (isCommon == true)
                {
                    foreach (var key in mapGroupAndProps.Keys)
                    {
                        mapGroupAndProps[key].Add(arg);
                    }

                    // give an error about the action argument being a string and using a common attribute with that.
                    if (actionArg != null && (actionArg.Symbol as IPropertySymbol).Type.BaseType?.SpecialType != SpecialType.System_Enum)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(CommonArgumentAttributeUsedWhenActionArgumentNotEnumRule, arg.Symbol.Locations.First()));
                    }
                }
                else
                {
                    // if we have found an attribute that specifies a group, add it to that.
                    if (argGroup.Count > 0)
                    {
                        // add the current argument to all the argument groups defined.
                        foreach (var item in argGroup)
                        {
                            if (!mapGroupAndProps.TryGetValue(item, out List<Argument> args))
                            {
                                args = new List<Argument>();
                                mapGroupAndProps[item] = args;
                            }
                            
                            args.Add(arg);
                        }
                    }
                    else
                    {
                        //add it to the default one.
                        mapGroupAndProps[string.Empty].Add(arg);
                    }
                }
            }

            return mapGroupAndProps;
        }

        private static ActionArgument GetActionArgument(ImmutableArray<ISymbol> typeMembers, SymbolAnalysisContext context)
        {
            ActionArgument aa = null;
            foreach (var member in typeMembers)
            {
                var attributes = member.GetAttributes();
                if (!attributes.Any())
                {
                    continue;
                }

                foreach (var attribute in attributes)
                {
                    if (attribute.AttributeClass.Name == "ActionArgumentAttribute")
                    {
                        if (aa != null)
                        {
                            // we already found another action argument attribute
                            context.ReportDiagnostic(Diagnostic.Create(DuplicateActionArgumentRule, member.Locations.First()));
                        }
                        aa = new ActionArgument();
                        aa.Symbol = member;

                        var memberAsProperty = member as IPropertySymbol;
                        if (memberAsProperty == null)
                        {
                            // we don't have a type just yet.
                            continue;
                        }

                        if (memberAsProperty.Type.BaseType?.SpecialType == SpecialType.System_Enum)
                        {
                            var members = (member as IPropertySymbol).Type.GetMembers();

                            foreach (var item in members)
                            {
                                if (item is IFieldSymbol)
                                {
                                    aa.Values.Add(item.Name);
                                }
                            }
                        }
                    }
                }
            }

            return aa;
        }

        private static void ValidateDefinedProperties(Dictionary<string, List<Argument>> mapGroupArgs, ActionArgument actionArg, SymbolAnalysisContext context)
        {
            // for each group, validate the parameters.
            foreach (var groupName in mapGroupArgs.Keys)
            {
                ValidateArguments(mapGroupArgs[groupName], context);
            }
        }

        private static void ValidateArguments(List<Argument> args, SymbolAnalysisContext context)
        {
            HashSet<string> namesOfAllArgs = new HashSet<string>();
            HashSet<int> positionsOrRequiredArgs = new HashSet<int>();
            foreach (var item in args)
            {
                if (item is OptionalArgument)

                {
                    OptionalArgument oag = item as OptionalArgument;

                    // Validate that the same name is not used across required and optional arguments.
                    if (namesOfAllArgs.Contains(oag.Name))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(DuplicateArgumentNameRule, oag.Symbol.Locations.First(), oag.Name));
                    }

                    namesOfAllArgs.Add(oag.Name);
                }
                else if (item is RequiredArgument)
                {
                    RequiredArgument rag = item as RequiredArgument;

                    // Validate that the same position is not used twice 
                    if (positionsOrRequiredArgs.Contains(rag.Position))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(DuplicatePositionalArgumentPositionRule, rag.Symbol.Locations.First(), rag.Position));
                    }

                    // Validate that the same name is not used across required and optional arguments.
                    if (namesOfAllArgs.Contains(rag.Name))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(DuplicateArgumentNameRule, rag.Symbol.Locations.First(), rag.Name));
                    }

                    namesOfAllArgs.Add(rag.Name);
                    positionsOrRequiredArgs.Add(rag.Position);
                }
            }
        }

        /// <summary>
        /// Check to see if any of the members on the type have an attribute that is coming from the CommandLine assembly
        /// </summary>
        /// <param name="namedTypeSymbol"></param>
        /// <returns></returns>
        private static bool IsCommandLineArgumentClass(INamedTypeSymbol namedTypeSymbol)
        {
            foreach (var member in namedTypeSymbol.GetMembers())
            {
                // if there aren't attributes defined on the member, nothing to do.
                var attributes = member.GetAttributes();
                if (!attributes.Any())
                {
                    continue;
                }

                foreach (var attribute in attributes)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals("CommandLine", attribute.AttributeClass.ContainingAssembly.Name))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
