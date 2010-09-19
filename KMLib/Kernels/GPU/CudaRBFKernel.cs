﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnAnalytics.LinearAlgebra;
using GASS.CUDA;
using GASS.CUDA.Types;
using System.IO;

namespace KMLib.Kernels.GPU
{

    /// <summary>
    /// Class for computing RBF kernel using cuda.
    /// 
    /// </summary>
    public class CudaRBFKernel: VectorKernel<SparseVector>, IDisposable
    {
         private const string cudaModuleName = "cudaSVMKernels.cubin";
        const string cudaKernelName = "rbfCsrFormatKernel";
        //const string cudaKernelName = "linearCsrFormatKernelShared";
        const string cudaMainVecTexRefName = "mainVectorTexRef";
        const string cudaLabelsTexRefName = "labelsTexRef";
        const string cudaSelfDotTexRefName = "selfDotTexRef";



        /// <summary>
        /// linear kernel for normal product
        /// </summary>
        private LinearKernel linKernel;

        #region cuda types

        /// <summary>
        /// Cuda .net class for cuda opeation
        /// </summary>
        private CUDA cuda;


        /// <summary>
        /// cuda loaded module
        /// </summary>
        CUmodule cuModule;

        /// <summary>
        /// cuda kernel function
        /// </summary>
        CUfunction cuFunc;


        /// <summary>
        /// Cuda device pointer to vectors values
        /// </summary>
        CUdeviceptr valsPtr;
        /// <summary>
        /// cuda devie pointer to vectors indexes
        /// </summary>
        CUdeviceptr idxPtr;
        /// <summary>
        /// cuda device pointer to vectors lenght
        /// </summary>
        CUdeviceptr vecLenghtPtr;

        /// <summary>
        /// cuda device pointer for output
        /// </summary>
        CUdeviceptr outputPtr;

        /// <summary>
        /// cuda reference to texture for main problem element(vector), 
        /// </summary>
        CUtexref cuMainVecTexRef;

        CUdeviceptr mainVecPtr;

        /// <summary>
        /// cuda refeerenc to texture for labels
        /// </summary>
        CUtexref cuLabelsTexRef;
       
        /// <summary>
        /// cuda pointer to labels, neded for coping to texture
        /// </summary>
        private CUdeviceptr labelsPtr;

        /// <summary>
        /// cuda array neded for copy vector to texture
        /// </summary>
       // CUarray cuMainVecArray;

        /// <summary>
        /// cuda array neded for copy labels to texture
        /// </summary>
       // CUarray cuLabelsArray;
        #endregion

        /// <summary>
        /// vector for computing kernel product with other vectors
        /// </summary>
        /// <remarks>all the time this vector will be modified and copied to cuda array</remarks>
        float[] mainVector;


        /// <summary>
        /// Array for self dot product 
        /// </summary>
        float[] selfLinDot;


        /// <summary>
        /// average vector lenght, its only a heuristic
        /// </summary>
        private int avgVectorLenght = 50;

        static int threadsPerBlock = 256;
        static int blocksPerGrid = -1;

        /// <summary>
        /// last parameter offset in cuda function kernel for changing mainVectorIdx
        /// </summary>
        private int lastParameterOffset;
        /// <summary>
        /// index of current main problem element (vector)
        /// </summary>
        private uint mainVectorIdx=1;
        private int Gamma;
       

        public override SparseVector[] ProblemElements
        {
            set
            {
                if (value == null) throw new ArgumentNullException("value");
                linKernel.ProblemElements = value;

                base.ProblemElements = value;

                blocksPerGrid = (value.Length + threadsPerBlock - 1) / threadsPerBlock;
            }
        }


        public CudaRBFKernel()
        {
            linKernel = new LinearKernel();

        }

        public override float Product(SparseVector element1, SparseVector element2)
        {

            float x1Squere = linKernel.Product(element1, element1);
            float x2Squere = linKernel.Product(element2, element2);

            float dot = linKernel.Product(element1, element2);

            float prod = (float)Math.Exp(-Gamma * (x1Squere + x2Squere - 2 * dot));

            return prod;
            
        }

        public override float Product(int element1, int element2)
        {
           if (element1 >= problemElements.Length)
                throw new IndexOutOfRangeException("element1 out of range");

            if (element2 >= problemElements.Length)
                throw new IndexOutOfRangeException("element2 out of range");


            if (element1 == element2 && (DiagonalDotCacheBuilded))
                return DiagonalDotCache[element1];

             float x1Squere = selfLinDot[element1];
            float x2Squere  = selfLinDot[element2];
            //float x1Squere = linKernel.Product(element1, element1);
            //float x2Squere = linKernel.Product(element2, element2);

            float dot = linKernel.Product(element1, element2);

            float prod = (float)Math.Exp(-Gamma * (x1Squere + x2Squere - 2 * dot));

            return prod;
        }

        public override ParameterSelection<SparseVector> CreateParameterSelection()
        {
            throw   new NotImplementedException();
            //return new RbfParameterSelection();
        }

        public override void AllProducts(int element1,  float[] results)
        {

            //cuda calculation
            //todo: possible small improvements
            //if mainVectorIdx==element1 then we don't have to copy to device
            SparseVector mainVec = problemElements[element1];

            if (mainVectorIdx != element1)
            {
                Array.Clear(mainVector, 0, mainVector.Length);
                for (int j = 0; j < mainVec.mValueCount; j++)
                {
                    int idx = mainVec.mIndices[j];
                    float val = (float)mainVec.mValues[j];
                    mainVector[idx] = val;
                }

                cuda.CopyHostToDevice(mainVecPtr, mainVector);

                

            }
            uint align = cuda.SetTextureAddress(cuMainVecTexRef, mainVecPtr, (uint)(sizeof(float) * mainVector.Length));

            //copy to texture
           // cuda.CopyHostToArray(cuMainVecArray, mainVector, 0);
            

            //set the last parameter for kernel
            mainVectorIdx = (uint)element1;
            cuda.SetParameter(cuFunc, lastParameterOffset, mainVectorIdx);

            cuda.Launch(cuFunc, blocksPerGrid, 1);

            cuda.SynchronizeContext();
            cuda.CopyDeviceToHost(outputPtr, results);

            

        }


        public override void Init()
        {
            base.Init();

            //transform elements to specific array format -> CSR http://en.wikipedia.org/wiki/Sparse_matrix#Compressed_sparse_row_.28CSR_or_CRS.29
            //

            //list for all vectors values
            List<float> vecValsL = new List<float>(problemElements.Length * avgVectorLenght);

            //list for all vectors indexes
            List<int> vecIdxL = new List<int>(problemElements.Length * avgVectorLenght);

            //list of lenght of each vector, list of pointers
            List<int> vecLenghtL = new List<int>(problemElements.Length);

            //arrays for values, indexes and lenght
            float[] vecVals;
            int[] vecIdx;
            int[] vecLenght;


            int vecStartIdx = 0;
            for (int i = 0; i < problemElements.Length; i++)
            {
                var vec = problemElements[i];


                //!!! It has error, because vector  not always has onyl one zero element at the end
                //// mValues and mIndices have extra zero element at the end, so 
                ////after conversion we have to remove last element from the end
                //vecValsL.AddRange(Array.ConvertAll<double, float>(vec.mValues, Convert.ToSingle));
                ////removeing last zero element
                //vecValsL.RemoveAt(vecValsL.Count - 1);

                //coping and converting from double to float
                //double[] tmpArr = new double[vec.mValueCount];
                //Array.Copy(vec.mValues, tmpArr, vec.mValueCount);
                //vecValsL.AddRange(Array.ConvertAll<double, float>(vec.mValues, Convert.ToSingle));

                //coping and converting from double to float using Linq
                var converted = vec.mValues.Select(x => Convert.ToSingle(x)).Take(vec.mValueCount);
                vecValsL.AddRange(converted);

                

                vecIdxL.AddRange(vec.mIndices.Take(vec.mValueCount));
                //removeing last zero element
                //vecIdxL.RemoveAt(vecIdxL.Count - 1);


                vecLenghtL.Add(vecStartIdx);
                vecStartIdx += vec.mValueCount;
            }

            //for last index
            vecLenghtL.Add(vecStartIdx);

            //convert list to arrays
            vecVals = vecValsL.ToArray();
            vecIdx = vecIdxL.ToArray();
            vecLenght = vecLenghtL.ToArray();

            //set list reference to null to free memeory
            vecIdxL = null;
            vecLenghtL = null;
            vecValsL = null;

            #region cuda initialization

            cuda = new CUDA(0, true);
            //copy data to device, set cuda function parameters
            valsPtr = cuda.CopyHostToDevice(vecVals);
            idxPtr = cuda.CopyHostToDevice(vecIdx);
            vecLenghtPtr = cuda.CopyHostToDevice(vecLenght);

            //alocate memory on device
            //productResults = new float[problemElements.Length];
            //outputPtr = cuda.Allocate(productResults);

            outputPtr = cuda.Allocate((uint)(sizeof(float) * problemElements.Length));

            cuModule = cuda.LoadModule(Path.Combine(Environment.CurrentDirectory, cudaModuleName));
            cuFunc = cuda.GetModuleFunction(cudaKernelName);

            #endregion

            #region cuda set function parameters
            cuda.SetFunctionBlockShape(cuFunc, threadsPerBlock, 1, 1);

            int offset = 0;
            cuda.SetParameter(cuFunc, offset, valsPtr.Pointer);
            offset += IntPtr.Size;
            cuda.SetParameter(cuFunc, offset, idxPtr.Pointer);
            offset += IntPtr.Size;

            cuda.SetParameter(cuFunc, offset, vecLenghtPtr.Pointer);
            offset += IntPtr.Size;

            cuda.SetParameter(cuFunc, offset, outputPtr.Pointer);
            offset += IntPtr.Size;

            cuda.SetParameter(cuFunc, offset, (uint)problemElements.Length);
            offset += sizeof(int);

            lastParameterOffset = offset;
            cuda.SetParameter(cuFunc, offset, (uint)mainVectorIdx);

            

            offset += sizeof(int);
            cuda.SetParameterSize(cuFunc, (uint)offset);

           // cuda.UseRuntimeExceptions = false;
           // cuda.SetFunctionSharedSize(cuFunc, (uint)(sizeof(float) * problemElements[0].Count));

            //var cuerr = CUDARuntime.cudaGetLastError();
            //string errMsg = CUDARuntime.cudaGetErrorString(cuerr);
            //var err= cuda.LastError;
            #endregion

            //get reference to cuda texture for main vector
            cuMainVecTexRef = cuda.GetModuleTexture(cuModule, cudaMainVecTexRefName);
            //cuda.SetTextureFlags(cuMainVecTexRef, 0);

            //allocate memory for main vector, size of this vector is the same as dimenson, so many 
            //indexes will be zero, but cuda computation is faster
            mainVector = new float[problemElements[0].Count];
            mainVecPtr = cuda.CopyHostToDevice(mainVector);

            //create cuda array and bind to texture
            //cuMainVecArray = cuda.CreateArray(mainVector);
            //cuda.SetTextureArray(cuMainVecTexRef, cuMainVecArray);

            cuLabelsTexRef = cuda.GetModuleTexture(cuModule, cudaLabelsTexRefName);
            labelsPtr = cuda.CopyHostToDevice(Labels);
            uint align= cuda.SetTextureAddress(cuLabelsTexRef, labelsPtr, (uint)(sizeof(float) * Labels.Length));

         
        }


        #region IDisposable Members

        public void Dispose()
        {
            if (cuda != null)
            {
                //free all resources
                cuda.Free(valsPtr);
                cuda.Free(idxPtr);
                cuda.Free(outputPtr);
                cuda.Free(vecLenghtPtr);

                cuda.Free(labelsPtr);
                cuda.DestroyTexture(cuLabelsTexRef);

                cuda.Free(mainVecPtr);

                cuda.DestroyTexture(cuMainVecTexRef);

                cuda.Dispose();
                cuda = null;
            }
        }

        #endregion
    }
}