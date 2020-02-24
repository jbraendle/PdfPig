﻿namespace UglyToad.PdfPig.Writer
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using Content;
    using Core;
    using CrossReference;
    using Encryption;
    using Filters;
    using Logging;
    using Merging;
    using Parser;
    using Parser.FileStructure;
    using Parser.Parts;
    using Tokenization.Scanner;
    using Tokens;
    using UglyToad.PdfPig.Exceptions;
    using UglyToad.PdfPig.Graphics.Operations;
    using UglyToad.PdfPig.Writer.Fonts;

    /// <summary>
    /// Merges PDF documents into each other.
    /// </summary>
    public static class PdfMerger
    {
        private static readonly ILog Log = new NoOpLog();

        private static readonly IFilterProvider FilterProvider = new MemoryFilterProvider(new DecodeParameterResolver(Log),
            new PngPredictor(), Log);

        /// <summary>
        /// Merge two PDF documents together with the pages from <see cref="file1"/>
        /// followed by <see cref="file2"/>.
        /// </summary>
        public static byte[] Merge(string file1, string file2)
        {
            if (file1 == null)
            {
                throw new ArgumentNullException(nameof(file1));
            }

            if (file2 == null)
            {
                throw new ArgumentNullException(nameof(file2));
            }

            return Merge(new[]
            {
                File.ReadAllBytes(file1),
                File.ReadAllBytes(file2)
            });
        }

        /// <summary>
        /// Merge the set of PDF documents.
        /// </summary>
        public static byte[] Merge(IReadOnlyList<byte[]> files)
        {
            if (files == null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            const bool isLenientParsing = true;

            using var documentBuilder = new DocumentBuilder();

            foreach (var file in files)
            {
                var inputBytes = new ByteArrayInputBytes(file);
                var coreScanner = new CoreTokenScanner(inputBytes);

                var version = FileHeaderParser.Parse(coreScanner, true, Log);

                var bruteForceSearcher = new BruteForceSearcher(inputBytes);
                var xrefValidator = new XrefOffsetValidator(Log);
                var objectChecker = new XrefCosOffsetChecker(Log, bruteForceSearcher);

                var crossReferenceParser = new CrossReferenceParser(Log, xrefValidator, objectChecker, new Parser.Parts.CrossReference.CrossReferenceStreamParser(FilterProvider));

                var crossReferenceOffset = FileTrailerParser.GetFirstCrossReferenceOffset(inputBytes, coreScanner, isLenientParsing);

                var objectLocations = bruteForceSearcher.GetObjectLocations();

                CrossReferenceTable crossReference = null;

                var locationProvider = new ObjectLocationProvider(() => crossReference, bruteForceSearcher);
                // I'm not using the BruteForceObjectLocationProvider because, the offset that it give are wrong by +2
                // var locationProvider = new BruteForcedObjectLocationProvider(objectLocations);

                var pdfScanner = new PdfTokenScanner(inputBytes, locationProvider, FilterProvider, NoOpEncryptionHandler.Instance);

                crossReference = crossReferenceParser.Parse(inputBytes, isLenientParsing, crossReferenceOffset, version.OffsetInFile, pdfScanner, coreScanner);

                var trailerDictionary = crossReference.Trailer;

                var (trailerRef, catalogDictionaryToken) = ParseCatalog(crossReference, pdfScanner, out var encryptionDictionary);

                if (encryptionDictionary != null)
                {
                    // TODO: Find option of how to pass password for the documents...
                    throw new PdfDocumentEncryptedException("Unable to merge document with password");
                    // pdfScanner.UpdateEncryptionHandler(new EncryptionHandler(encryptionDictionary, trailerDictionary, new[] { string.Empty }));
                }

                var objectsTree = new ObjectsTree(trailerDictionary, pdfScanner.Get(trailerRef),
                    CatalogFactory.Create(crossReference.Trailer.Root, catalogDictionaryToken, pdfScanner, isLenientParsing));

                var objectsLocation = bruteForceSearcher.GetObjectLocations();

                var root = pdfScanner.Get(trailerDictionary.Root);

                var tokens = new List<IToken>();

                pdfScanner.Seek(0);
                while (pdfScanner.MoveNext())
                {
                    tokens.Add(pdfScanner.CurrentToken);
                }

                if (!(tokens.Count == objectLocations.Count))
                {
                    // Do we really need to check this?
                    throw new PdfDocumentFormatException("Something whent wrong while reading file");
                }

                documentBuilder.AppendNewDocument(objectsTree, pdfScanner);
            }

            return documentBuilder.Build();
        }

        // This method is a basically a copy of the method UglyToad.PdfPig.Parser.PdfDocumentFactory.ParseTrailer()
        private static (IndirectReference, DictionaryToken) ParseCatalog(CrossReferenceTable crossReferenceTable,
            IPdfTokenScanner pdfTokenScanner,
            out EncryptionDictionary encryptionDictionary)
        {
            encryptionDictionary = null;

            if (crossReferenceTable.Trailer.EncryptionToken != null)
            {
                if (!DirectObjectFinder.TryGet(crossReferenceTable.Trailer.EncryptionToken, pdfTokenScanner,
                    out DictionaryToken encryptionDictionaryToken))
                {
                    throw new PdfDocumentFormatException($"Unrecognized encryption token in trailer: {crossReferenceTable.Trailer.EncryptionToken}.");
                }

                encryptionDictionary = EncryptionDictionaryFactory.Read(encryptionDictionaryToken, pdfTokenScanner);
            }

            var rootDictionary = DirectObjectFinder.Get<DictionaryToken>(crossReferenceTable.Trailer.Root, pdfTokenScanner);

            if (!rootDictionary.ContainsKey(NameToken.Type))
            {
                rootDictionary = rootDictionary.With(NameToken.Type, NameToken.Catalog);
            }

            return (crossReferenceTable.Trailer.Root, rootDictionary);
        }

        // Note: I don't think making this a disposable is a good idea.
        // Also, suggestion for name?
        private class DocumentBuilder : IDisposable
        {
            private bool isDisposed = false;

            private MemoryStream Memory = new MemoryStream();

            private readonly BuilderContext Context = new BuilderContext();

            private readonly List<IndirectReferenceToken> DocumentPages = new List<IndirectReferenceToken>();

            private IndirectReferenceToken RootPagesIndirectReference;

            public DocumentBuilder()
            {
                var reserved = Context.ReserveNumber();
                RootPagesIndirectReference = new IndirectReferenceToken(new IndirectReference(reserved, 0));

                WriteHeaderToStream();
            }

            private void WriteHeaderToStream()
            {
                // Copied from UglyToad.PdfPig.Writer.PdfDocumentBuilder
                WriteString("%PDF-1.7", Memory);

                // Files with binary data should contain a 2nd comment line followed by 4 bytes with values > 127
                Memory.WriteText("%");
                Memory.WriteByte(169);
                Memory.WriteByte(205);
                Memory.WriteByte(196);
                Memory.WriteByte(210);
                Memory.WriteNewLine();
            }
 
            public void AppendNewDocument(ObjectsTree newDocument, IPdfTokenScanner tokenScanner)
            {
                if (isDisposed)
                {
                    throw new ObjectDisposedException("Merger disposed already");
                }

                /*
                 * I decided that I want to have an /Pages object for each document's pages. That way I avoided resource name conflict
                 * But I guess that doesn't matter either way? So that part can be eliminated?
                 */
                var pageReferences = ConstructPageReferences(newDocument.Catalog.PageTree, tokenScanner);

                var pagesDictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
                {
                    { NameToken.Type, NameToken.Pages },
                    { NameToken.Kids, new ArrayToken(pageReferences) },
                    { NameToken.Count, new NumericToken(pageReferences.Count) },
                    { NameToken.Parent, RootPagesIndirectReference }
                });

                var pagesRef = Context.WriteObject(Memory, pagesDictionary);
                DocumentPages.Add(new IndirectReferenceToken(pagesRef.Number));
            }

            private IReadOnlyList<IndirectReferenceToken> ConstructPageReferences(PageTreeNode treeNode, IPdfTokenScanner tokenScanner)
            {
                var reserved = Context.ReserveNumber();
                var parentIndirect = new IndirectReferenceToken(new IndirectReference(reserved, 0));

                var pageReferences = new List<IndirectReferenceToken>();
                foreach (var pageNode in treeNode.Children)
                {
                    if (!pageNode.IsPage)
                    {
                        var nestedPageReferences = ConstructPageReferences(pageNode, tokenScanner);
                        var pagesDictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
                        {
                            { NameToken.Type, NameToken.Pages },
                            { NameToken.Kids, new ArrayToken(nestedPageReferences) },
                            { NameToken.Count, new NumericToken(nestedPageReferences.Count) },
                            { NameToken.Parent, parentIndirect }
                        });

                        var pagesRef = Context.WriteObject(Memory, pagesDictionary);
                        pageReferences.Add(new IndirectReferenceToken(pagesRef.Number));
                        continue;
                    }

                    var pageDictionary = new Dictionary<NameToken, IToken>
                    {
                        {NameToken.Parent, parentIndirect},
                    };

                    foreach(var setPair in pageNode.NodeDictionary.Data)
                    {
                        var name = setPair.Key;
                        var token = setPair.Value;

                        if (name == NameToken.Parent)
                        {
                            // Skip Parent token, since we have to reassign it
                            continue;
                        }

                        pageDictionary.Add(NameToken.Create(name), CopyToken(token, tokenScanner));
                    }

                    var pageRef = Context.WriteObject(Memory, new DictionaryToken(pageDictionary), reserved);
                    pageReferences.Add(new IndirectReferenceToken(pageRef.Number));
                }

                return pageReferences;
            }

            private IToken CopyToken(IToken tokenToCopy, IPdfTokenScanner tokenScanner)
            {
                if (tokenToCopy is DictionaryToken dictionaryToken)
                {
                    var newContent = new Dictionary<NameToken, IToken>();
                    foreach (var setPair in dictionaryToken.Data)
                    {
                        var name = setPair.Key;
                        var token = setPair.Value;
                        newContent.Add(NameToken.Create(name), CopyToken(token, tokenScanner));
                    }

                    return new DictionaryToken(newContent);
                }
                else if (tokenToCopy is ArrayToken arrayToken)
                {
                    var newArray = new List<IToken>(arrayToken.Length);
                    foreach (var token in arrayToken.Data)
                    {
                        newArray.Add(CopyToken(token, tokenScanner));
                    }

                    return new ArrayToken(newArray);
                }
                else if (tokenToCopy is IndirectReferenceToken referenceToken)
                {
                    var tokenObject = DirectObjectFinder.Get<IToken>(referenceToken.Data, tokenScanner);
                    
                    // Is this even a allowed?
                    Debug.Assert(!(tokenObject is IndirectReferenceToken));

                    var newToken = CopyToken(tokenObject, tokenScanner);
                    var objToken = Context.WriteObject(Memory, newToken);
                    return new IndirectReferenceToken(objToken.Number);
                }
                else
                {
                    // TODO: Should we do a deep copy of the token?
                    return tokenToCopy;
                }
            }

            public byte[] Build()
            {
                if (isDisposed)
                {
                    throw new ObjectDisposedException("Merger disposed already");
                }

                if (DocumentPages.Count < 1)
                {
                    throw new PdfDocumentFormatException("Empty document");
                }

                var pagesDictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
                {
                    { NameToken.Type, NameToken.Pages },
                    { NameToken.Kids, new ArrayToken(DocumentPages) },
                    { NameToken.Count, new NumericToken(DocumentPages.Count) }
                });

                var pagesRef = Context.WriteObject(Memory, pagesDictionary, (int)RootPagesIndirectReference.Data.ObjectNumber);

                var catalog = new DictionaryToken(new Dictionary<NameToken, IToken>
                {
                    { NameToken.Type, NameToken.Catalog },
                    { NameToken.Pages, new IndirectReferenceToken(pagesRef.Number) }
                });

                var catalogRef = Context.WriteObject(Memory, catalog);

                TokenWriter.WriteCrossReferenceTable(Context.ObjectOffsets, catalogRef, Memory, null);
                
                var bytes = Memory.ToArray();

                Dispose();

                return bytes;
            }

            // Note: This method is copied from UglyToad.PdfPig.Writer.PdfDocumentBuilder
            private static void WriteString(string text, MemoryStream stream, bool appendBreak = true)
            {
                var bytes = OtherEncodings.StringAsLatin1Bytes(text);
                stream.Write(bytes, 0, bytes.Length);
                if (appendBreak)
                {
                    stream.WriteNewLine();
                }
            }

            public void Dispose()
            {
                if (isDisposed)
                    return;

                Memory.Dispose();
                Memory = null;
                isDisposed = true;
            }
        }

        // Currently unused becauase, brute force search give the wrong offset (+2)
        private class BruteForcedObjectLocationProvider : IObjectLocationProvider
        {
            private readonly Dictionary<IndirectReference, long> objectLocations;
            private readonly Dictionary<IndirectReference, ObjectToken> cache = new Dictionary<IndirectReference, ObjectToken>();

            public BruteForcedObjectLocationProvider(IReadOnlyDictionary<IndirectReference, long> objectLocations)
            {
                this.objectLocations = objectLocations.ToDictionary(x => x.Key, x => x.Value);
            }

            public bool TryGetOffset(IndirectReference reference, out long offset)
            {
                var result = objectLocations.TryGetValue(reference, out offset);
                //offset -= 2;
                return result;
            }

            public void UpdateOffset(IndirectReference reference, long offset)
            {
                objectLocations[reference] = offset;
            }

            public bool TryGetCached(IndirectReference reference, out ObjectToken objectToken)
            {
                return cache.TryGetValue(reference, out objectToken);
            }

            public void Cache(ObjectToken objectToken, bool force = false)
            {
                if (!TryGetOffset(objectToken.Number, out var offsetExpected) || force)
                {
                    cache[objectToken.Number] = objectToken;
                }

                if (offsetExpected != objectToken.Position)
                {
                    return;
                }

                cache[objectToken.Number] = objectToken;
            }
        }
    }
}