﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;

namespace KMLib.Helpers
{
    public class IOHelper
    {
        /// <summary>
        /// Reads vectos from file.
        /// File format like in LIbSVM
        /// label index:value index2:value .....
        /// -1        4:0.5       10:0.9 ....
        /// </summary>
        /// <param name="fileName">Data set file name</param>
        /// <returns></returns>
        public static Problem<SparseVec> ReadVectorsFromFile(string fileName, int numberOfFeatures)
        {
            //initial list capacity 8KB, its only heuristic
            int listCapacity = 1 << 13;

            //if maxIndex is grether than numberOfFeatures
            int indexAboveFeature = 0;

            //list of labels
            List<float> labels = new List<float>(listCapacity);

            //counts how many labels we have
            Dictionary<float, int> coutLabels = new Dictionary<float, int>(10);

            //vector parts (index and value) separator
            char[] vecPartsSeparator = new char[] { ' ' };
            //separator between index and value in one part
            char[] idxValSeparator = new char[] { ':' };
            int max_index = numberOfFeatures;

            List<KeyValuePair<int, float>> vec = new List<KeyValuePair<int, float>>(32);

            //list of Vectors, currently use SparseVector implementation from dnAnalitycs
            List<SparseVec> dnaVectors = new List<SparseVec>(listCapacity);

            using (FileStream fileStream = File.OpenRead(fileName))
            {
                using (StreamReader input = new StreamReader(fileStream))
                {
                    //todo: string split function to many memory allocation, http://msdn.microsoft.com/en-us/library/b873y76a.aspx
                    while (input.Peek() > -1)
                    {
                        int indexSeparatorPosition = -1;
                        string inputLine = input.ReadLine().Trim();

                        int index = 0;

                        float value = 0;

                        //add one space to the end of line, needed for parsing
                        string oneLine = new StringBuilder(inputLine).Append(" ").ToString();

                        int partBegin = -1, partEnd = -1;

                        partBegin = oneLine.IndexOf(vecPartsSeparator[0]);
                        //from begining to first space is label
                        float dataLabel = float.Parse(oneLine.Substring(0, partBegin), CultureInfo.InvariantCulture);
                        labels.Add(dataLabel);

                        if (coutLabels.ContainsKey(dataLabel))
                            coutLabels[dataLabel]++;
                        else
                            coutLabels[dataLabel] = 1;

                        index = -1;

                        value = -1;
                        partEnd = oneLine.IndexOf(vecPartsSeparator[0], partBegin + 1);

                        while (partEnd > 0)
                        {

                            indexSeparatorPosition = oneLine.IndexOf(idxValSeparator[0], partBegin);
                            index = int.Parse(oneLine.Substring(partBegin + 1, indexSeparatorPosition - (partBegin + 1)));

                            if (index < 1)
                            {
                                throw new ArgumentOutOfRangeException("indexes should start from 1 not from 0");
                            }

                            value = float.Parse(oneLine.Substring(indexSeparatorPosition + 1, partEnd - (indexSeparatorPosition + 1)), CultureInfo.InvariantCulture);


                            vec.Add(new KeyValuePair<int, float>(index, value));
                            partBegin = partEnd;
                            partEnd = oneLine.IndexOf(vecPartsSeparator[0], partBegin + 1);

                        }

                        if (vec.Count > 0)
                        {
                            max_index = Math.Max(max_index, index);

                        }

                        //we implictie set numberOfFeatures if max_index is less then numberOfFeatures
                        if (max_index <= numberOfFeatures)
                            max_index = numberOfFeatures;
                        else
                        {
                            //how many previosus vectors has wrong (less) dim size
                            indexAboveFeature = dnaVectors.Count;
                        }

                        dnaVectors.Add(new SparseVec(max_index, vec));

                        //clear vector parts
                        vec.Clear();
                    }//end while
                }


            }


            for (int i = 0; i < indexAboveFeature; i++)
            {
                dnaVectors[i].Dim = max_index;
            }

            int numberOfClasses = coutLabels.Count;
            var elementClasses = coutLabels.Keys.ToArray();

            return new Problem<SparseVec>(dnaVectors.ToArray(), labels.ToArray(), max_index,numberOfClasses,elementClasses);
        }
    }
}
