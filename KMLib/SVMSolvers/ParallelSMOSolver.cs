﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KMLib.Kernels;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using KMLib.Helpers;
using System.Diagnostics;

namespace KMLib.SVMSolvers
{
    /// <summary>
    /// Modified version of J. Plat SMO solver for SVM, 
    /// Simultaniously take step according to vectors which are in different direction,
    /// on each processor core its computed one step
    /// </summary>
    public class ParallelSMOSolver<TProblemElement> : Solver<TProblemElement>
    {

        static Random random = new Random();

        //float C = 0.05f;
        float tolerance = 0.01f;
        float epsilon = 0.01f;

        /// <summary>Array of Lagrange multipliers alpha_i</summary>
        protected float[] alpha;

        /// <summary>
        /// Threshold.
        /// </summary>
        protected float b = 0f;

        private float delta_b = 0f;
        protected float[] errorCache;


        /// <summary>
        /// stores non zero alphas indexes
        /// </summary>
        // List<int> nonZeroAlphas = new List<int>();

        /// <summary>Cache of Gram matrix diagonal.</summary>
        // protected float[] diagGramCache;


        public ParallelSMOSolver(Problem<TProblemElement> problem, IKernel<TProblemElement> kernel, float C)
            : base(problem, kernel, C)
        {

        }


        public override Model<TProblemElement> ComputeModel()
        {
            errorCache = new float[problem.ElementsCount];
            alpha = new float[problem.ElementsCount];
            // SMO algorithm
            int numChange = 0;
            int examineAll = 1;
            int cores = System.Environment.ProcessorCount;

            float E1 = float.MinValue;
            float y1, alpha1;

            long mainIter = 0;
            long subIter = 0;


            while (numChange > 0 || examineAll > 0)
            {
                numChange = 0;

                mainIter++;



                if (examineAll > 0)
                {
                    for (int k = 0; k < problem.ElementsCount; k++)
                    {


                        y1 = problem.Y[k];
                        alpha1 = alpha[k];
                        E1 = ErrorForAlpha(k);

                        //find first index
                        if (!KKTViolator(E1, y1, alpha1))
                            continue;

                        subIter++;

                        AlphaInfo st1 = new AlphaInfo(k, alpha1, y1, E1, Product(k, k));

                        var newSteps = FindAlphaSteps(cores, st1);

                        if (newSteps.Count == 0)
                            continue;
                        else
                            numChange += newSteps.Count;

                        var newAlphas = MergeSteps(st1, newSteps);


                        UpdateAlpha(newAlphas);

                    }
                }
                else
                {
                    for (int k = 0; k < problem.ElementsCount; k++)
                    {

                        if (alpha[k] == 0 || alpha[k] == C)
                            continue;


                        y1 = problem.Y[k];
                        alpha1 = alpha[k];
                        E1 = ErrorForAlpha(k);

                        //find first index
                        if (!KKTViolator(E1, y1, alpha1))
                            continue;

                        subIter++;

                        AlphaInfo st1 = new AlphaInfo(k, alpha1, y1, E1, Product(k, k));

                        var newSteps = FindAlphaSteps(cores, st1);

                        if (newSteps.Count == 0)
                            continue;
                        else
                            numChange += newSteps.Count;

                        var newAlphas = MergeSteps(st1, newSteps);


                        UpdateAlpha(newAlphas);

                    }
                }


                if (examineAll == 1)
                {
                    examineAll = 0;
                }
                else if (numChange == 0)
                {
                    examineAll = 1;
                }


            }

            // cleaning
            errorCache = null;

            #region Building Model
            Model<TProblemElement> model = new Model<TProblemElement>();
            model.NumberOfClasses = 2;
            model.Alpha = alpha;
            model.Bias = b;

            List<TProblemElement> supportElements = new List<TProblemElement>(alpha.Length);
            List<int> suporrtIndexes = new List<int>(alpha.Length);
            List<float> supportLabels = new List<float>(alpha.Length);
            for (int j = 0; j < alpha.Length; j++)
            {
                if (Math.Abs(alpha[j]) > 0)
                {
                    supportElements.Add(problem.Elements[j]);
                    suporrtIndexes.Add(j);
                    supportLabels.Add(problem.Y[j]);
                }

            }
            model.SupportElements = supportElements.ToArray();
            model.SupportElementsIndexes = suporrtIndexes.ToArray();
            model.Y = supportLabels.ToArray();
           
            #endregion


            Debug.WriteLine("Main iteration=" + mainIter + " subIteration=" + subIter);
            Console.WriteLine("Main iteration=" + mainIter + " subIteration=" + subIter);
            Console.WriteLine("SV=" + model.SupportElementsIndexes.Length);

            return model;
        }

        private void UpdateAlpha(AlphaInfo[] newAlphas)
        {
            for (int i = 0; i < newAlphas.Length; i++)
            {
                var st = newAlphas[i];
                st.AlphaStep = st.Y * (st.Alpha - alpha[st.Index]);
            }

            var rangePart = Partitioner.Create(0, problem.ElementsCount);

            Parallel.ForEach(rangePart, (range, loopState) =>
            {

                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (0 < alpha[i] && alpha[i] < C)
                    {
                        for (int j = 0; j < newAlphas.Length; j++)
                        {
                            int alphaIndex = newAlphas[j].Index;
                            errorCache[i] += newAlphas[j].AlphaStep * Product(i, alphaIndex);
                        }
                        errorCache[i] -= delta_b;
                    }
                }
            });

            //update erroCache for alpha's
            for (int j = 0; j < newAlphas.Length; j++)
            {
                int alphaIndex = newAlphas[j].Index;
                //zero error on modified alphas
                errorCache[alphaIndex] = 0f;

                //update alphas
                alpha[alphaIndex] = newAlphas[j].Alpha;
            }
        }

        private List<StepPairVariable> FindAlphaSteps(int cores, AlphaInfo st1)
        {
            List<StepPairVariable> newAlphas = new List<StepPairVariable>(cores);

            object lockObj = new object();

            int k = st1.Index;


            #region Parallel version


            Parallel.ForEach(GlobalHeuristic(k), (i2, loopState) =>
            {
                if (loopState.ShouldExitCurrentIteration)
                    return;

                StepPairVariable ap = ComputeAlphaStep(st1, i2);

                if (ap == null)
                    return;

                lock (lockObj)
                {
                    if (newAlphas.Count >= cores)
                    {
                        //stop searching
                        loopState.Stop();
                        return;
                    }
                    else
                    {
                        newAlphas.Add(ap);

                        if (newAlphas.Count >= cores)
                            loopState.Stop();
                    }
                }

            });


            #endregion
            return newAlphas;
        }

        private AlphaInfo[] MergeSteps(AlphaInfo st1, List<StepPairVariable> newAlphas)
        {
            //a1 - new alpha, alpha1-old alpha
            float a1 = 0f, alpha1 = 0;

            int index1 = st1.Index;
            float y1 = st1.Y;
            alpha1 = alpha[st1.Index];
            float k11 = st1.Product,
                E1 = st1.Error;

            int size = newAlphas.Count + 1;

            AlphaInfo[] modAlphas = new AlphaInfo[size];


            a1 = WeightedEtaMerge(st1, newAlphas, modAlphas);

            if (a1 < 0)
            {
                for (int i = 1; i < size; i++)
                {
                    modAlphas[i].Alpha += newAlphas[i - 1].Si * a1;
                }

                //a2 += s * a1;
                a1 = 0;
            }
            else if (a1 > C)
            {
                float t = a1 - C;

                for (int i = 1; i < size; i++)
                {
                    modAlphas[i].Alpha += newAlphas[i - 1].Si * t;
                }

                a1 = C;
            }

            modAlphas[0].Alpha = a1;

            float bnew = 0;

            if (a1 > 0 && a1 < C)
            {
                bnew = b + E1 + y1 * (a1 - alpha1) * k11;
                for (int i = 1; i < size; i++)
                {
                    var item = modAlphas[i];

                    float al2 = item.Alpha;

                    int ind = item.Index;

                    float oldAl = alpha[ind];

                    bnew += item.Y * (al2 - oldAl) * newAlphas[i - 1].Product;

                }
            }
            else
            {
                for (int i = 1; i < size; i++)
                {
                    var item = modAlphas[i];
                    float prodI1 = newAlphas[i - 1].Product;

                    if (item.Alpha > 0 && item.Alpha < C)
                    {
                        int mIndex = item.Index;

                        bnew = item.Error + b + y1 * (a1 - alpha1) * prodI1;
                        for (int j = 1; j < modAlphas.Length; j++)
                        {
                            var st = modAlphas[j];
                            bnew += st.Y * (st.Alpha - alpha[st.Index]) * Product(mIndex, st.Index);

                        }
                        //we compute new B;
                        break;
                    }
                }

            }


            //todo: plus or minus?
            delta_b = bnew - b;
            b = bnew;

            return modAlphas;



        }

        /// <summary>
        /// 
        /// Scala listę kroków stosując średnią ważoną gdzie wagą jest Eta
        /// </summary>
        /// <param name="st1"></param>
        /// <param name="newAlphas"></param>
        /// <param name="a1"></param>
        /// <param name="modAlphas">new merged alpha</param>
        /// <param name="weighSum"></param>
        /// <returns>average value for a1</returns>
        private float WeightedEtaMerge(AlphaInfo st1, List<StepPairVariable> newAlphas, AlphaInfo[] modAlphas)
        {
            int size = modAlphas.Length;
            float weighSum = 0;
            float a1 = 0f;

            modAlphas[0] = new AlphaInfo(st1.Index, 0, st1.Y, st1.Error, st1.Product);

            float[] weights = new float[size];

            for (int k = 1; k < size; k++)
            {
                var secAlpha = newAlphas[k - 1].Second;
                modAlphas[k] = new AlphaInfo(secAlpha.Index, 0, secAlpha.Y, secAlpha.Error, secAlpha.Product);
                weighSum += newAlphas[k - 1].Eta;

                weights[k] = Math.Abs(newAlphas[k - 1].Eta);
            }

            weighSum = Math.Abs(weighSum);
            a1 = MergeWithWeight(newAlphas, modAlphas, weighSum, weights);
            return a1;
        }


        private float WeightedSmallEtaMerge(AlphaInfo st1, List<StepPairVariable> newAlphas, AlphaInfo[] modAlphas)
        {
            int size = modAlphas.Length;
            float weighSum = 0;
            float a1 = 0f;

            modAlphas[0] = new AlphaInfo(st1.Index, 0, st1.Y, st1.Error, st1.Product);

            float[] weights = new float[size];

            for (int k = 1; k < size; k++)
            {
                var secAlpha = newAlphas[k - 1].Second;
                modAlphas[k] = new AlphaInfo(secAlpha.Index, 0, secAlpha.Y, secAlpha.Error, secAlpha.Product);
                weighSum += 1.0f / newAlphas[k - 1].Eta;

                weights[k] = Math.Abs(1.0f / newAlphas[k - 1].Eta);
            }

            weighSum = Math.Abs(weighSum);
            a1 = MergeWithWeight(newAlphas, modAlphas, weighSum, weights);
            return a1;
        }


        private float AvgMerge(AlphaInfo st1, List<StepPairVariable> newAlphas, AlphaInfo[] modAlphas)
        {
            int size = modAlphas.Length;
            float weighSum = 0;
            float a1 = 0f;

            modAlphas[0] = new AlphaInfo(st1.Index, 0, st1.Y, st1.Error, st1.Product);

            float[] weights = new float[size];

            float weight = 1.0f / newAlphas.Count;

            for (int k = 1; k < size; k++)
            {
                var secAlpha = newAlphas[k - 1].Second;
                modAlphas[k] = new AlphaInfo(secAlpha.Index, 0, secAlpha.Y, secAlpha.Error, secAlpha.Product);


                weights[k] = weight;
            }

            weighSum = 1;
            a1 = MergeWithWeight(newAlphas, modAlphas, weighSum, weights);
            return a1;
        }


        /// <summary>
        /// 
        /// Scala listę kroków stosując średnią ważoną gdzie wagą jest długość kroku
        /// </summary>
        /// <param name="st1"></param>
        /// <param name="newAlphas"></param>
        /// <param name="a1"></param>
        /// <param name="modAlphas">new merged alpha</param>
        /// <param name="weighSum"></param>
        /// <returns>average value for a1</returns>
        private float WeightedDistMerge(AlphaInfo st1, List<StepPairVariable> newAlphas, AlphaInfo[] modAlphas)
        {
            int size = modAlphas.Length;
            float weighSum = 0;
            float a1 = 0f;

            float[] weights = new float[size];

            modAlphas[0] = new AlphaInfo(st1.Index, 0, st1.Y, st1.Error, st1.Product);


            for (int k = 1; k < size; k++)
            {
                var secAlpha = newAlphas[k - 1].Second;
                modAlphas[k] = new AlphaInfo(secAlpha.Index, 0, secAlpha.Y, secAlpha.Error, secAlpha.Product);



                var it = newAlphas[k - 1];

                //we compute change in distance for two alphas
                float sq1 = it.First.Alpha - alpha[it.First.Index];
                sq1 *= sq1;
                float sq2 = it.Second.Alpha - alpha[it.Second.Index];
                sq2 *= sq2;
                weights[k] = sq1 + sq2;

                weighSum += weights[k];

            }

            weighSum = Math.Abs(weighSum);
            a1 = MergeWithWeight(newAlphas, modAlphas, weighSum, weights);
            return a1;
        }


        private float MergeWithWeight(List<StepPairVariable> newAlphas, AlphaInfo[] modAlphas, float weighSum, float[] weights)
        {
            float weight1 = 0f, weight2 = 0f;
            int size = modAlphas.Length;
            float a1 = 0;
            //weigheted average
            for (int i = 1; i < size; i++)
            {
                var item = newAlphas[i - 1];
                //weight1 = Math.Abs(item.Eta);
                weight1 = weights[i];

                a1 += weight1 * item.First.Alpha;

                modAlphas[i].Alpha += item.Second.Alpha * weight1;

                for (int j = i + 1; j < size; j++)
                {
                    var item2 = newAlphas[j - 1];
                    //weight2 = Math.Abs(item2.Eta);
                    weight2 = weights[j];
                    modAlphas[i].Alpha += weight2 * alpha[item.Second.Index];

                    modAlphas[j].Alpha += weight1 * alpha[item2.Second.Index];
                }

                modAlphas[i].Alpha /= weighSum;

            }

            a1 /= weighSum;
            return a1;
        }



        private bool KKTViolator(float error, float yi, float alpha)
        {
            float r1 = yi * error;

            // Outer loop: choosing the first element.
            // First heuristic: testing for violation of KKT conditions
            if ((r1 < -tolerance && alpha < C) || (r1 > tolerance && alpha > 0))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="i1">index for first alpha</param>
        /// <param name="E1">decision error for first alpah</param>
        /// <param name="y1">label coresponding to first alpha</param>
        /// <param name="alph1">alpha</param>
        /// <param name="i2">index second alpha</param>
        /// <returns></returns>
        private StepPairVariable ComputeAlphaStep(AlphaInfo step1, int i2)
        {

            float y1 = 0, y2 = 0, s = 0;
            float alph1, alph2 = 0; /* old_values of alpha_1, alpha_2 */
            float a1 = 0, a2 = 0; /* new values of alpha_1, alpha_2 */
            float E1 = 0, E2 = 0, //error for second alpha
                L = 0, H = 0,  //lower upper bounds
                k11 = 0, k22 = 0, k12 = 0, //kernel products
                eta = 0,  // step taken,
                Lobj = 0, Hobj = 0;

            int i1 = step1.Index;

            if (i1 == i2) return null; // no step taken

            alph1 = step1.Alpha;
            y1 = step1.Y;
            E1 = step1.Error;


            alph2 = alpha[i2];
            y2 = problem.Y[i2];
            E2 = ErrorForAlpha(i2);

            s = y1 * y2;

            //compute bounds
            if (y1 == y2)
            {
                float gamma = alph1 + alph2;
                if (gamma > C)
                {
                    L = gamma - C;
                    H = C;
                }
                else
                {
                    L = 0;
                    H = gamma;
                }
            }
            else
            {
                float gamma = alph2 - alph1;
                if (gamma > 0)
                {
                    L = gamma;
                    H = C;
                }
                else
                {
                    L = 0;
                    H = C - gamma;
                }
            }

            if (L == H)
            {
                return null; // no step take
            }

            k11 = step1.Product;


            k12 = Product(i1, i2);
            k22 = Product(i2, i2);
            eta = 2 * k12 - k11 - k22;


            if (eta < 0)
            {
                //original version with plus  
                //a2 = alph2 + y2 * (E2 - E1) / eta;

                a2 = alph2 - y2 * (E1 - E2) / eta;

                if (a2 < L) a2 = L;
                else if (a2 > H) a2 = H;
            }
            else
            {
                {
                    float c1 = eta / 2;
                    float c2 = y2 * (E1 - E2) - eta * alph2;
                    Lobj = c1 * L * L + c2 * L;
                    Hobj = c1 * H * H + c2 * H;
                }

                if (Lobj > Hobj + epsilon) a2 = L;
                else if (Lobj < Hobj - epsilon) a2 = H;
                else a2 = alph2;
            }

            if (Math.Abs(a2 - alph2) < epsilon * (a2 + alph2 + epsilon))
            {
                return null; // no step taken
            }

            a1 = alph1 - s * (a2 - alph2);
            if (a1 < 0)
            {
                a2 += s * a1;
                a1 = 0;
            }
            else if (a1 > C)
            {
                float t = a1 - C;
                a2 += s * t;
                a1 = C;
            }

            var st1 = new AlphaInfo(i1, a1, y1, E1, k11);
            var st2 = new AlphaInfo(i2, a2, y2, E2, k22);
            return new StepPairVariable(st1, st2, k12, s, eta);
        }

        private IEnumerable<int> GlobalHeuristic(int index)
        {
            int i2max = -1;
            float tmax = 0;

            //first max, then group of indexes for not bound alphas, last bound alphas
            LinkedList<int> groupedIndexes = new LinkedList<int>();

            float E1 = ErrorForAlpha(index);

            int k0 = random.Next(problem.ElementsCount);
            //todo : remove this 
            k0 = 0;

            for (int k = k0; k < problem.ElementsCount + k0; k++)
            {
                int i = k % problem.ElementsCount;

                if (alpha[i] > 0 && alpha[i] < C)
                {
                    float E2 = errorCache[i];
                    float temp = Math.Abs(E1 - E2);

                    if (temp > tmax)
                    {
                        tmax = temp;
                        i2max = i;

                        groupedIndexes.AddFirst(i2max);
                    }
                    else
                    {
                        if (groupedIndexes.First != null)
                            groupedIndexes.AddAfter(groupedIndexes.First, i);
                        else
                            groupedIndexes.AddFirst(i);
                    }
                }
                else
                {
                    groupedIndexes.AddLast(i);
                }


            }

            foreach (var item in groupedIndexes)
            {
                yield return item;
            }


        }

        /// <summary>
        /// Computes Error for alpha_i
        /// </summary>
        /// <param name="i"></param>
        /// <returns></returns>
        private float ErrorForAlpha(int i)
        {
            float y1 = problem.Y[i],
                  alph1 = alpha[i],
                  E1 = 0;

            if (alph1 > 0 && alph1 < C)
            {
                E1 = errorCache[i];
            }
            else
            {
                E1 = DecisionFunc(i) - y1;
            }
            return E1;
        }


        /// <summary>This method solves the two Lagrange multipliers problem.</summary>
        /// <returns>Indicates if the step has been taken.</returns>
        private AlphaPair TakeStep(int i1, int i2)
        {

            float y1 = 0, y2 = 0, s = 0;
            float alph1 = 0, alph2 = 0; /* old_values of alpha_1, alpha_2 */
            float a1 = 0, a2 = 0; /* new values of alpha_1, alpha_2 */
            float E1 = 0, E2 = 0, L = 0, H = 0, k11 = 0, k22 = 0, k12 = 0, eta = 0, Lobj = 0, Hobj = 0;

            if (i1 == i2) return null; // no step taken

            alph1 = alpha[i1];
            y1 = problem.Y[i1];
            E1 = ErrorForAlpha(i1);

            alph2 = alpha[i2];
            y2 = problem.Y[i2];
            E2 = ErrorForAlpha(i2);

            s = y1 * y2;


            if (y1 == y2)
            {
                float gamma = alph1 + alph2;
                if (gamma > C)
                {
                    L = gamma - C;
                    H = C;
                }
                else
                {
                    L = 0;
                    H = gamma;
                }
            }
            else
            {
                float gamma = alph2 - alph1;
                if (gamma > 0)
                {
                    L = gamma;
                    H = C;
                }
                else
                {
                    L = 0;
                    H = C - gamma;
                }
            }

            if (L == H)
            {
                return null; // no step take
            }

            k11 = Product(i1, i1);
            k12 = Product(i1, i2);
            k22 = Product(i2, i2);
            eta = 2 * k12 - k11 - k22;


            if (eta < 0)
            {
                a2 = alph2 - y2 * (E1 - E2) / eta;

                if (a2 < L) a2 = L;
                else if (a2 > H) a2 = H;
            }
            else
            {
                {
                    float c1 = eta / 2;
                    float c2 = y2 * (E1 - E2) - eta * alph2;
                    Lobj = c1 * L * L + c2 * L;
                    Hobj = c1 * H * H + c2 * H;
                }

                if (Lobj > Hobj + epsilon) a2 = L;
                else if (Lobj < Hobj - epsilon) a2 = H;
                else a2 = alph2;
            }

            if (Math.Abs(a2 - alph2) < epsilon * (a2 + alph2 + epsilon))
            {
                return null; // no step taken
            }

            a1 = alph1 - s * (a2 - alph2);
            if (a1 < 0)
            {
                a2 += s * a1;
                a1 = 0;
            }
            else if (a1 > C)
            {
                float t = a1 - C;
                a2 += s * t;
                a1 = C;
            }


            float b1 = 0, b2 = 0, bnew = 0;

            if (a1 > 0 && a1 < C)
                bnew = b + E1 + y1 * (a1 - alph1) * k11 + y2 * (a2 - alph2) * k12;
            else
            {
                if (a2 > 0 && a2 < C)
                    bnew = b + E2 + y1 * (a1 - alph1) * k12 + y2 * (a2 - alph2) * k22;
                else
                {
                    b1 = b + E1 + y1 * (a1 - alph1) * k11 + y2 * (a2 - alph2) * k12;
                    b2 = b + E2 + y1 * (a1 - alph1) * k12 + y2 * (a2 - alph2) * k22;
                    bnew = (b1 + b2) / 2;
                }
            }

            return new AlphaPair() { FirstIndex = i1, FirstAlpha = a1, SecondIndex = i2, SecondAlpha = a2, Threshold = bnew };

        }


        /// <summary>
        /// Helper method for computing Kernel Product
        /// </summary>
        /// <param name="i1"></param>
        /// <param name="i"></param>
        /// <returns></returns>
        private float Product(int i1, int i)
        {

            return kernel.Product(i1, i);

        }


        /// <summary>
        /// Compute decision function for k'th element
        /// </summary>
        /// <param name="k"></param>
        /// <returns></returns>
        private float DecisionFunc(int k)
        {

            float sum = 0;
            //sum( apha_i*y_i*K(x_i,problemElement)) + b
            //sum can by compute only on support vectors
            for (int i = 0; i < problem.Elements.Length; i++)
            {
                if (alpha[i] != 0)
                    sum += alpha[i] * problem.Y[i] * Product(i, k);
            }

            sum -= b;
            return sum;

        }

    }
}