﻿using EnvDTE80;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace inline_glsl
{
	/// <summary>
	/// Classifier that classifies all text as an instance of the "ShaderClassifier" classification type.
	/// </summary>
	internal class ShaderClassifier : IClassifier
	{
		public delegate void ResponseDelegate(string s);
		[DllImport("glslang_wrapper.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
		public static extern void compile_shader(string shader, int stage, ResponseDelegate response);


		public ShaderTagger tagger = null;
		public ErrorListProvider errorList;
		/// <summary>
		/// Classification type.
		/// </summary>
		private readonly IClassificationType keywordsClassifier;
		private readonly IClassificationType controlKeywordsClassifier;
		private readonly IClassificationType preprocessorKeywordsClassifier;
		private readonly IClassificationType commentsClassifier;
		private readonly IClassificationType identifiers1Classifier;
		private readonly IClassificationType identifiers2Classifier;
		private readonly IClassificationType identifiers3Classifier;
		private readonly IClassificationType operatorsClassifier;
		private readonly IClassificationType numbersClassifier;
		private readonly IClassificationType excludedCodeClassifier;
		private readonly IClassificationType textClassifier;
		private readonly IClassificationType typeClassifier;
		private readonly IClassificationType functionsClassifier;

		public IStandardClassificationService Classifications;

		/// <summary>
		/// Initializes a new instance of the <see cref="ShaderClassifier"/> class.
		/// </summary>
		/// <param name="registry">Classification registry.</param>
		internal ShaderClassifier(IClassificationTypeRegistryService registry, IStandardClassificationService classifications, ITextBuffer buffer)
		{
			this.keywordsClassifier = registry.GetClassificationType("ShaderKeywords");
			this.controlKeywordsClassifier = registry.GetClassificationType("ShaderControlKeywords");
			this.preprocessorKeywordsClassifier = registry.GetClassificationType("ShaderPreprocessorKeywords");
			this.commentsClassifier = registry.GetClassificationType("ShaderComments");
			this.identifiers1Classifier = registry.GetClassificationType("ShaderIdentifiers1");
			this.identifiers2Classifier = registry.GetClassificationType("ShaderIdentifiers2");
			this.identifiers3Classifier = registry.GetClassificationType("ShaderIdentifiers3");
			this.operatorsClassifier = registry.GetClassificationType("ShaderOperators");
			this.numbersClassifier = registry.GetClassificationType("ShaderNumbers");
			this.excludedCodeClassifier = registry.GetClassificationType("ShaderExcludedCode");
			this.textClassifier = registry.GetClassificationType("ShaderText");
			this.typeClassifier = registry.GetClassificationType("ShaderType");
			this.functionsClassifier = registry.GetClassificationType("ShaderFunctions");
			Classifications = classifications;
			this.errorList = new ErrorListProvider(ServiceProvider.GlobalProvider);
		}

		#region IClassifier

#pragma warning disable 67

		/// <summary>
		/// An event that occurs when the classification of a span of text has changed.
		/// </summary>
		/// <remarks>
		/// This event gets raised if a non-text change would affect the classification in some way,
		/// for example typing /* would cause the classification to change in C# without directly
		/// affecting the span.
		/// </remarks>
		public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;
		
#pragma warning restore 67

		public enum StringLiteralType
		{
			Invalid = 0, EscapedNewlines, AdjacentStrings, RawString
		}
		public enum ShaderType
		{
			Invalid = 0, Vertex, Fragment, Compute, TessellationControl, TessellationEvaluation, Geometry
		}

		public class GlslData
		{
			public ITrackingSpan span;
			public ITextSnapshot snapshot;
			public ShaderType shaderType;
			public StringLiteralType literalType;
			public List<(ITrackingSpan, string)> errors;
			public GlslData()
			{
			}

			public GlslData(ITrackingSpan span, StringLiteralType literalType, ShaderType shaderType)
			{
				errors = new List<(ITrackingSpan, string)>();
				this.span = span;
				this.literalType = literalType;
				this.shaderType = shaderType;
			}
		}

		public Dictionary<int, GlslData> glsl_strings = new Dictionary<int, GlslData>();

		public void UpdateErrors(int line)
		{
			var string_type = glsl_strings[line].literalType;
			StringBuilder compiler_input = new StringBuilder();
			compiler_input.Append(new string('\n', line-1));
			foreach (var cur_line in glsl_strings[line].span.GetSpan(glsl_strings[line].snapshot).GetText().Split(new[] { "\n\r", "\n" }, StringSplitOptions.None))
			{
				var l = cur_line;
				if(l.EndsWith("\r")) l = l.Remove(l.Length - 1);
				if (l.EndsWith("\\0")) l = l.Remove(l.Length - 2);
				if (string_type == StringLiteralType.EscapedNewlines && l.EndsWith("\\n\\"))
				{
					l = l.Remove(l.Length - 3);
				}
				if (string_type == StringLiteralType.AdjacentStrings && l.EndsWith("\\n\""))
				{
					l = l.Remove(l.Length - 3);
				}
				if (string_type == StringLiteralType.AdjacentStrings && l.TrimStart().StartsWith("\""))
				{
					var arr = l.ToCharArray();
					arr[l.IndexOf('"')] = ' ';
					l = new string(arr);
				}
				if (string_type == StringLiteralType.RawString)
				{
					// I've found nothing that needs to be done for raw string, but I'll leave this if here for completeness' sake
				}
				compiler_input.AppendLine(l);
			}
			string compiler_input_str = compiler_input.ToString();
			compile_shader(compiler_input_str, (int)glsl_strings[line].shaderType, s =>
			{
				glsl_strings[line].errors.Clear();
				errorList.Tasks.Clear();
				foreach (var e in s.Split("\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries))
				{
					if (!e.Contains("ERROR:") || e.Contains("compilation terminated") || e.Contains("No code generated.")) continue;
					var loc_match = Regex.Match(e, "[0-9]+:[0-9]+", RegexOptions.IgnoreCase);
					if (!loc_match.Success) continue;
					var loc = (from n in loc_match.Value.Split(':') select int.Parse(n)).ToList();
					var line_span = glsl_strings[line].snapshot.GetLineFromLineNumber(loc[1]);

					var start = line_span.Start;
					while ((char.IsWhiteSpace(start.GetChar()) || 
							(start.GetChar() == '"' && string_type == StringLiteralType.AdjacentStrings)
						) && start < line_span.End
					) start += 1;

					glsl_strings[line].errors.Add((
						glsl_strings[line].snapshot.CreateTrackingSpan(
							new SnapshotSpan(start, line_span.End).Span,
							SpanTrackingMode.EdgeInclusive),
						e
					));

					var err = new ErrorTask()
					{
						ErrorCategory = TaskErrorCategory.Error,
						Category = TaskCategory.BuildCompile,
						Text = e,
						//Document = line_span.Snapshot.TextBuffer.,
						Line = loc[1],
						Column = 0,
						//HierarchyItem = hierarchyItem
					};


					err.Navigate += (sender, args) =>
					{
						err.Line++;
						errorList.Navigate(err, Guid.Parse(EnvDTE.Constants.vsViewKindCode));
						err.Line--;
					};

					errorList.Tasks.Add(err);  // add item
				}
				errorList.Show();
			});

			tagger?.raiseTagsChanged(new SnapshotSpanEventArgs(glsl_strings[line].span.GetSpan(glsl_strings[line].snapshot)));
		}

		public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
		{
			//declare glsl tokens
			string[] glsl_keywords = new string[] { "attribute", "const", "uniform",
					"varying", "centroid","in","out",
					"inout","float","int","void","bool","true","false",
					"invariant","mat2","mat3","mat4",
					"mat2x2","mat2x3","mat2x4","mat3x2","mat3x3","mat3x4",
					"mat4x2","mat4x3","mat4x4","vec2","vec3","vec4","ivec2",
					"ivec3","ivec4","bvec2","bvec3","bvec4","sampler1D",
					"sampler2D","sampler3D","samplerCube","sampler1DShadow",
					"sampler2DShadow","struct"};

			string[] glsl_control_keywords = new string[] {
				"break","continue", "do","for","while","if","else","discard","return"
			};

			string[] glsl_builtin_functions = new string[] {
				"radians", "degrees", "sin", "cos", "tan", "asin", "acos",
				"atan", "atan", "pow", "exp", "log", "exp2", "log2", "sqrt",
				"inversesqrt", "abs", "sign", "floor", "ceil", "fract", "mod",
				"min", "max", "clamp", "mix", "step", "smoothstep", "length",
				"distance", "dot", "cross", "normalize", "ftransform", "faceforward",
				"reflect", "refract", "matrixCompMult", "outerProduct", "transpose",
				"lessThan", "lessThanEqual", "greaterThan", "greaterThanEqual", "equal",
				"notEqual", "any", "all", "not", "texture1D", "texture1DProj",
				"texture1DProj", "texture1DLod", "texture1DProjLod", "texture1DProjLod",
				"texture2D", "texture2DProj", "texture2DProj", "texture2DLod",
				"texture2DProjLod", "texture2DProjLod", "texture3D", "texture3DProj",
				"texture3DLod", "texture3DProjLod", "textureCube", "textureCubeLod",
				"shadow1D", "shadow2D", "shadow1DProj", "shadow2DProj", "shadow1DLod",
				"shadow2DLod", "shadow1DProjLod", "shadow2DProjLod", "dFdx", "dFdy",
				"fwidth", "noise1", "noise2", "noise3", "noise4", "inverse"
			}; 

			//list containing regions(spans) and their colours
			var result = new List<ClassificationSpan>();

			//the line on which the current span is
			var line = span.Snapshot.GetLineNumberFromPosition(span.End);
			
			//use a comment like "// vertex shader:" to denote where glsl should be highlighted
			var comment_match = Regex.Match(
				span.GetText(),
                //"\\/\\/[ \t]*(vertex|fragment|compute|tesc|tese|geometry)[ \t]+shader[ \t]*:?|\\/\\*[ \t]*(vertex|fragment|compute|tesc|tese|geometry)[ \t]+shader[ \t]*:?\\*\\/",
                "\\/\\/[ \t]*(vertex|fragment|compute|tesc|tese|geometry|tessellation[ \t]+control|tessellation[ \t]+evaluation)[ \t]+shader[ \t]*:?|\\/\\*[ \t]*(vertex|fragment|compute|tesc|tese|geometry|tessellation[ \t]+control|tessellation[ \t]+evaluation)[ \t]+shader[ \t]*:?\\*\\/",
                RegexOptions.IgnoreCase
			);

			if (comment_match.Success)
			{
				// mark the comment as a keyword
				result.Add(new ClassificationSpan(new SnapshotSpan(span.Snapshot,
					new Span(span.Start + comment_match.Index, comment_match.Length)),
					typeClassifier
				));
				ShaderType shader_type = ShaderType.Invalid;
				if (comment_match.Value.ToLower().Contains("vertex")) shader_type = ShaderType.Vertex;
				if (comment_match.Value.ToLower().Contains("fragment")) shader_type = ShaderType.Fragment;
				if (comment_match.Value.ToLower().Contains("compute")) shader_type = ShaderType.Compute;
				if (comment_match.Value.ToLower().Contains("tesc") || comment_match.Value.ToLower().Contains("tessellation control")) shader_type = ShaderType.TessellationControl;
				if (comment_match.Value.ToLower().Contains("tese") || comment_match.Value.ToLower().Contains("tessellation evaluation")) shader_type = ShaderType.TessellationEvaluation;
				if (comment_match.Value.ToLower().Contains("geometry")) shader_type = ShaderType.Geometry;
				// now find the shader string
				var index = span.Start + comment_match.Index + comment_match.Length;
				// find the opening quotes
				while (span.Snapshot[index] != '"' || span.Snapshot[index - 1] == '\\')
				{
					// if following the comment is something other than a string, then discard everything
					// the only exception is R, because it denotes a raw string
					if (!char.IsWhiteSpace(span.Snapshot[index]) && span.Snapshot[index] != 'R')
					{
						if (glsl_strings.ContainsKey(line)) glsl_strings.Remove(line);
						return new List<ClassificationSpan>();
					}
					index = index + 1;
				}
				StringLiteralType string_type = StringLiteralType.Invalid;
				SnapshotPoint string_start;
				SnapshotPoint string_end = new SnapshotPoint();
				if (span.Snapshot[index - 1] == 'R')
				{
					//this is a raw string
					string_type = StringLiteralType.RawString;
					index = index + 1; // move away from quote
					//find the first perenthesis, and store the delimiter along the way
					List<char> delimiter = new List<char>();
					while (span.Snapshot[index] != '(')
					{
						delimiter.Add(span.Snapshot[index]);
						index = index + 1;
					}
					//since we're trying to find the end, and the delimiter is always followed by a quote,
					//we're adding the quote here, so we can find it along with the delimiter
					delimiter.Add('"');
					index = index + 1; // move away from the perenthesis
					string_start = index; 
					while (index < span.Snapshot.Length - delimiter.Count)
					{
						while (index < span.Snapshot.Length - delimiter.Count && span.Snapshot[index] != ')')
							index = index + 1;
						bool valid = true;
						for (int i = 0; i < delimiter.Count; i++)
						{
							if (span.Snapshot[index + 1 + i] != delimiter[i])
							{
								valid = false;
								break;
							}
						}
						if (valid) break;
						index = index + 1;
					}
					if (index >= span.Snapshot.Length)
					{
						if (glsl_strings.ContainsKey(line)) glsl_strings.Remove(line);
						return new List<ClassificationSpan>();
					}
					string_end = index;
				} else
				{
					index = index + 1; // move away from the opening quotes
					string_start = index;
					bool first = true;
					//loop finding strings until there are no more adjacent strings
					while (index < span.Snapshot.Length)
					{
						// find the closing quotes
						while (span.Snapshot[index] != '"' || span.Snapshot[index - 1] == '\\')
						{
							if (span.Snapshot[index] == '\n' && !(span.Snapshot[index - 1] == '\\' || span.Snapshot[index - 2] == '\\'))
							{
								//non-escaped newline - terminate
								if (glsl_strings.ContainsKey(line)) glsl_strings.Remove(line);
								return new List<ClassificationSpan>();
							}
							index = index + 1;
						}
						//look ahead to see if there are adjacent strings
						var index2 = index + 1;
						while (span.Snapshot[index2] != '"')
						{
							if (!char.IsWhiteSpace(span.Snapshot[index2]))
							{
								if (first) //a single string, with escaped newlines
									string_type = StringLiteralType.EscapedNewlines;
								else
									string_type = StringLiteralType.AdjacentStrings;
								string_end = index;
								break;
							}
							index2 = index2 + 1;
						}
						if (string_type != StringLiteralType.Invalid) break; //we found the end of it
						index = index2 + 1;
						first = false;
					}
				}

				var snap_span = span.Snapshot.CreateTrackingSpan(new Span(string_start, string_end - string_start), SpanTrackingMode.EdgeInclusive);
				ClassificationChanged.Invoke(this, new ClassificationChangedEventArgs(snap_span.GetSpan(span.Snapshot)));
				if (!glsl_strings.ContainsKey(line)) glsl_strings.Add(line, new GlslData());

				glsl_strings[line] = new GlslData(snap_span, string_type, shader_type);

				glsl_strings[line].snapshot = span.Snapshot;
				UpdateErrors(line);
			} else if (glsl_strings.ContainsKey(line)) 
			{
				glsl_strings.Remove(line);
			}


			//passed the comment, or no comment on this line, is the span in the last of the glsl spans?
			var span_line = (from k in glsl_strings.Keys where k <= line select k).DefaultIfEmpty(-1).Max();
			if (span_line == -1) return result;

			// if the snapshot is different, update the error tags
			if (span.Snapshot != glsl_strings[span_line].snapshot)
			{
				//var d = glsl_strings[span_line];
				//d.span = new SnapshotSpan(span.Snapshot, glsl_strings[span_line].span.Span);
				//glsl_strings[span_line] = d;
				//ClassificationChanged.Invoke(this, new ClassificationChangedEventArgs(span.Snapshot.GetLineFromLineNumber(span_line).Extent));
				glsl_strings[span_line].snapshot = span.Snapshot;
				UpdateErrors(span_line);
			}
			//calculate what is actually inside the glsl string
			var inter_or_null = span.Intersection(glsl_strings[span_line].span.GetSpan(span.Snapshot));
			if (inter_or_null == null) return result;
			SnapshotSpan inter = (SnapshotSpan)inter_or_null;

			//make the escaped newline darker
			if (glsl_strings[span_line].literalType == StringLiteralType.EscapedNewlines)
			{
				var trimmed = inter.GetText().TrimEnd(new char[] { '\n','\r' });
				if (trimmed.EndsWith("\\n\\"))
				{
					result.Add(new ClassificationSpan(new SnapshotSpan(inter.Start + trimmed.Length - 3, 3),
						excludedCodeClassifier
					));
					inter = new SnapshotSpan(inter.Start, trimmed.Length - 3);
				}
			}
			else if (glsl_strings[span_line].literalType == StringLiteralType.AdjacentStrings)
			{
				var trimmed = inter.GetText().TrimEnd(new char[] { '\n', '\r' });
				if (trimmed.EndsWith("\\n\""))
				{
					result.Add(new ClassificationSpan(new SnapshotSpan(inter.Start + trimmed.Length - 3, 3),
						excludedCodeClassifier
					));
					inter = new SnapshotSpan(inter.Start, trimmed.Length - 3);
				}
			}

			int version = 0;
			bool comment = false;
			bool after_dot = false;
			//iterate all words
			for(var i = inter.Start; i < inter.End;)
			{
				//move away from whitespaces
				while (Char.IsWhiteSpace(i.GetChar())) i = i + 1;
				if (i >= inter.End) break;

				var end_of_token = i + 1;// new SnapshotSpan(i, inter.End).GetText().IndexOfAny(" \t".ToCharArray());
				char first_char = i.GetChar();

				IClassificationType c_type = textClassifier;

				//chars which are a token by themselves
				if ("-.,;[](){}".Contains(first_char))
				{
					if (first_char == '.')
					{
						after_dot = true;
					}
					c_type = operatorsClassifier;
					end_of_token = i + 1;
				}
				else if ("0123456789".Contains(first_char))
				{
					//numbers
					while (end_of_token < inter.End && "0123456789.".Contains(end_of_token.GetChar())) 
						end_of_token = end_of_token + 1;
					c_type = numbersClassifier;
				}
				else if ("_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".Contains(first_char))
				{
					//identifiers
					while (end_of_token < inter.End && "_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".Contains(end_of_token.GetChar())) 
						end_of_token = end_of_token + 1;
					if(after_dot)
					{
						after_dot = false;
						c_type = identifiers2Classifier;
					} else
					{
						c_type = identifiers1Classifier;
					}
				}
				if (first_char == '#')
				{
					//identifiers
					while (end_of_token < inter.End && "_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".Contains(end_of_token.GetChar())) 
						end_of_token = end_of_token + 1;
					c_type = preprocessorKeywordsClassifier;
				} else if(first_char == '/' && (i+1).GetChar() == '/')
				{
					comment = true;
				}
				var word = new SnapshotSpan(i, end_of_token);

				if(comment)
				{
					c_type = commentsClassifier;
				} else if (word.GetText().StartsWith("#version"))
				{
					c_type = preprocessorKeywordsClassifier;
					version = 1;
				} else if(version == 1)
				{
					c_type = numbersClassifier;
					version = 2;
				} else if (version == 2)
				{
					c_type = textClassifier;
					version = 0;
				} else if (glsl_keywords.Contains(word.GetText()))
				{
					c_type = keywordsClassifier;
				} else if (glsl_control_keywords.Contains(word.GetText()))
				{
					c_type = controlKeywordsClassifier;
				}
				else if (glsl_builtin_functions.Contains(word.GetText()))
				{
					c_type = functionsClassifier;
				}
				else if (word.GetText().StartsWith("gl_"))
				{
					c_type = identifiers3Classifier;
				}


				result.Add(new ClassificationSpan(word,
					c_type
				));
				i = end_of_token;
			}


			return result;
		}

		#endregion
	}
}