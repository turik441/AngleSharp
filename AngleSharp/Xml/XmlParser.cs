﻿using AngleSharp.DOM;
using AngleSharp.Events;
using AngleSharp.DOM.Xml;
using System;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace AngleSharp.Xml
{
    /// <summary>
    /// For more details: See the W3C Recommendation
    /// http://www.w3.org/TR/REC-xml/
    /// and a little bit about XML parser (XHTML context)
    /// http://www.w3.org/html/wg/drafts/html/master/the-xhtml-syntax.html#xml-parser.
    /// </summary>
    [DebuggerStepThrough]
    public sealed class XmlParser : IParser
    {
        #region Members

        XmlTokenizer tokenizer;
        Boolean started;
        XMLDocument doc;
        List<Element> open;
        XmlTreeMode insert;
        Task task;
        Boolean standalone;

        #endregion

        #region Events

        /// <summary>
        /// This event is raised once a parser error occured.
        /// </summary>
        public event ParseErrorEventHandler ErrorOccurred;

        #endregion

        #region ctor

        /// <summary>
        /// Creates a new instance of the XML parser with an new document
        /// based on the given source.
        /// </summary>
        /// <param name="source">The source code as a string.</param>
        public XmlParser(String source)
            : this(new XMLDocument(), new SourceManager(source))
        {
        }

        /// <summary>
        /// Creates a new instance of the XML parser with an new document
        /// based on the given stream.
        /// </summary>
        /// <param name="stream">The stream to use as source.</param>
        public XmlParser(Stream stream)
            : this(new XMLDocument(), new SourceManager(stream))
        {
        }

        /// <summary>
        /// Creates a new instance of the XML parser with the specified document
        /// based on the given source.
        /// </summary>
        /// <param name="document">The document instance to be constructed.</param>
        /// <param name="source">The source code as a string.</param>
        public XmlParser(XMLDocument document, String source)
            : this(document, new SourceManager(source))
        {
        }

        /// <summary>
        /// Creates a new instance of the XML parser with the specified document
        /// based on the given stream.
        /// </summary>
        /// <param name="document">The document instance to be constructed.</param>
        /// <param name="stream">The stream to use as source.</param>
        public XmlParser(XMLDocument document, Stream stream)
            : this(document, new SourceManager(stream))
        {
        }

        /// <summary>
        /// Creates a new instance of the XML parser with the specified document
        /// based on the given source manager.
        /// </summary>
        /// <param name="document">The document instance to be constructed.</param>
        /// <param name="source">The source to use.</param>
        internal XmlParser(XMLDocument document, SourceManager source)
        {
            tokenizer = new XmlTokenizer(source);

            tokenizer.ErrorOccurred += (s, ev) =>
            {
                if (ErrorOccurred != null)
                    ErrorOccurred(this, ev);
            };

            started = false;
            doc = document;
            standalone = false;
            open = new List<Element>();
            insert = XmlTreeMode.Initial;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the current node.
        /// </summary>
        internal Node CurrentNode
        {
            get { return open.Count > 0 ? (Node)open[open.Count - 1] : (Node)doc; }
        }

        /// <summary>
        /// Gets the (maybe intermediate) result of the parsing process.
        /// </summary>
        public XMLDocument Result
        {
            get
            {
                Parse();
                return doc;
            }
        }

        /// <summary>
        /// Gets if the XML is standalone.
        /// </summary>
        public Boolean Standalone
        {
            get { return standalone; }
        }

        /// <summary>
        /// Gets if the parser has been started asynchronously.
        /// </summary>
        public Boolean IsAsync
        {
            get { return task != null; }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Parses the given source asynchronously and creates the document.
        /// </summary>
        /// <returns>The task which could be awaited or continued differently.</returns>
        public Task ParseAsync()
        {
            if (!started)
            {
                started = true;
                task = Task.Run(() => Kernel());
            }
            else if (task == null)
                throw new InvalidOperationException("The parser has already run synchronously.");

            return task;
        }

        /// <summary>
        /// Parses the given source and creates the document.
        /// </summary>
        public void Parse()
        {
            if (!started)
            {
                started = true;
                Kernel();
            }
        }

        /// <summary>
        /// Consumes a token and processes it.
        /// </summary>
        /// <param name="token">The token to consume.</param>
        void Consume(XmlToken token)
        {
            switch (insert)
            {
                case XmlTreeMode.Initial:
                    Initial(token);
                    break;
                case XmlTreeMode.Prolog:
                    BeforeDoctype(token);
                    break;
                case XmlTreeMode.Body:
                    InBody(token);
                    break;
            }
        }

        #endregion

        #region States

        /// <summary>
        /// The initial state. Expects an XML declaration.
        /// </summary>
        /// <param name="token">The consumed token.</param>
        void Initial(XmlToken token)
        {
            if (token.Type == XmlTokenType.Declaration)
            {
                var tok = (XmlDeclarationToken)token;
                standalone = tok.Standalone;
                var ver = 1.0;

                if (!tok.IsEncodingMissing)
                    SetEncoding(tok.Encoding);

                //The declaration token -- Check version
                if (!Double.TryParse(tok.Version, NumberStyles.Any, CultureInfo.InvariantCulture, out ver) || ver >= 2.0)
                    throw Errors.GetException(ErrorCode.XmlDeclarationVersionUnsupported);
            }
            else if (!token.IsIgnorable)
            {
                RaiseErrorOccurred(ErrorCode.UndefinedMarkupDeclaration);
                insert = XmlTreeMode.Prolog;
                BeforeDoctype(token);
            }
        }

        /// <summary>
        /// Before any doctype - still in the prolog. No declaration
        /// allowed.
        /// </summary>
        /// <param name="token">The consumed token.</param>
        void BeforeDoctype(XmlToken token)
        {
            if (token.Type == XmlTokenType.DOCTYPE)
            {
                var tok = (XmlDoctypeToken)token;
                var doctype = new DocumentType();
                doctype.SystemId = tok.SystemIdentifier;
                doctype.PublicId = tok.PublicIdentifier;
                doctype.TypeDefinitions = tokenizer.DTD;
                doctype.Name = tok.Name;
                doc.AppendChild(doctype);
                insert = XmlTreeMode.Body;
            }
            else if (token.Type == XmlTokenType.ProcessingInstruction)
            {
                var tok = (XmlPIToken)token;
                var pi = doc.CreateProcessingInstruction(tok.Target, tok.Content);
                doc.AppendChild(pi);
            }
            else if (token.Type == XmlTokenType.Comment)
            {
                var tok = (XmlCommentToken)token;
                var com = doc.CreateComment(tok.Data);
                doc.AppendChild(com);
            }
            else if (!token.IsIgnorable)
            {
                insert = XmlTreeMode.Body;
                InBody(token);
            }
        }

        /// <summary>
        /// In the body state - no doctypes and declarations allowed.
        /// </summary>
        /// <param name="token">The consumed token.</param>
        void InBody(XmlToken token)
        {
            switch (token.Type)
            {
                case XmlTokenType.StartTag:
                {
                    var tok = (XmlTagToken)token;
                    var tag = doc.CreateElement(tok.Name);
                    CurrentNode.AppendChild(tag);

                    if (!tok.IsSelfClosing)
                        open.Add(tag);

                    for (int i = 0; i < tok.Attributes.Count; i++)
                        tag.SetAttribute(tok.Attributes[i].Key, tok.Attributes[i].Value);

                    break;
                }
                case XmlTokenType.EndTag:
                {
                    if (open.Count == 0)
                        throw Errors.GetException(ErrorCode.TagCannotEndHere);

                    var tok = (XmlTagToken)token;

                    if (CurrentNode.NodeName != tok.Name)
                        throw Errors.GetException(ErrorCode.TagClosingMismatch);

                    open.RemoveAt(open.Count - 1);
                    break;
                }
                case XmlTokenType.Comment:
                {
                    var tok = (XmlCommentToken)token;
                    var com = doc.CreateComment(tok.Data);
                    CurrentNode.AppendChild(com);
                    break;
                }
                case XmlTokenType.ProcessingInstruction:
                {
                    var tok = (XmlPIToken)token;
                    var pi = doc.CreateProcessingInstruction(tok.Target, tok.Content);
                    CurrentNode.AppendChild(pi);
                    break;
                }
                case XmlTokenType.Entity:
                {
                    var tok = (XmlEntityToken)token;
                    var str = tokenizer.GetEntity(tok);
                    CurrentNode.AppendText(str);
                    break;
                }
                case XmlTokenType.Character:
                {
                    var tok = (XmlCharacterToken)token;
                    CurrentNode.AppendText(tok.Data);
                    break;
                }
                case XmlTokenType.EOF:
                {
                    if (open.Count != 0)
                        throw Errors.GetException(ErrorCode.EOF);
                    else if (doc.DocumentElement == null)
                        throw Errors.GetException(ErrorCode.XmlMissingRoot);

                    break;
                }
                case XmlTokenType.DOCTYPE:
                {
                    throw Errors.GetException(ErrorCode.XmlDoctypeAfterContent);
                }
                case XmlTokenType.Declaration:
                {
                    throw Errors.GetException(ErrorCode.XmlDeclarationMisplaced);
                }
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// The kernel that is pulling the tokens into the parser.
        /// </summary>
        void Kernel()
        {
            XmlToken token;

            do
            {
                token = tokenizer.Get();
                Consume(token);
            }
            while (token.Type != XmlTokenType.EOF);
        }

        /// <summary>
        /// Sets the document's encoding to the given one.
        /// </summary>
        /// <param name="encoding">The encoding to use.</param>
        void SetEncoding(String encoding)
        {
            if (DocumentEncoding.IsSupported(encoding))
            {
                var enc = DocumentEncoding.Resolve(encoding);

                if (enc != null)
                {
                    doc.InputEncoding = enc.WebName;
                    tokenizer.Stream.Encoding = enc;
                }
            }
        }

        #endregion

        #region Handlers

        /// <summary>
        /// Fires an error occurred event.
        /// </summary>
        /// <param name="code">The associated error code.</param>
        void RaiseErrorOccurred(ErrorCode code)
        {
            if (ErrorOccurred != null)
            {
                var pck = new ParseErrorEventArgs((int)code, Errors.GetError(code));
                pck.Line = tokenizer.Stream.Line;
                pck.Column = tokenizer.Stream.Column;
                ErrorOccurred(this, pck);
            }
        }

        #endregion
    }
}
