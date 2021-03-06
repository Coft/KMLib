﻿/*
author: Krzysztof Sopyla
mail: krzysztofsopyla@gmail.com
License: MIT
web page: http://wmii.uwm.edu.pl/~ksopyla/projects/svm-net-with-cuda-kmlib/
*/

#include <float.h>

__constant__ float C;
// minimal coeficient
__constant__ float COEF_EPS = 0.000001f;

// constat for kernel diagonal for index i
//__constant__ float QD_i;

// label for i-th example
//__constant__ float Y_i;

//texture for vector, which is used for matrix vector multiplication
//in SVM, when we have to compute many dot products (one vector with others)
texture<float,1,cudaReadModeElementType> mainVectorTexRef;


#define BLOCK_SIZE 128

#define BLOCK_SIZE_RED 128


//define NEG_INFINITY_F __int_as_float(0xff800000)



/*

	Do warp parallel reduction in order to find max value and its index
*/
__device__ void maxWarpReduce(volatile int *volShIdx,volatile float *volShVal,unsigned int tid)
{
		/*
		if (BLOCK_SIZE_RED >=  64) { if( volShVal[tid]< volShVal[tid+32]) {
							 volShVal[tid]=volShVal[tid+32]; volShIdx[tid]=volShIdx[tid+32];	} }
		if (BLOCK_SIZE_RED >=  32) { if( volShVal[tid]< volShVal[tid+16]) {
							 volShVal[tid]=volShVal[tid+16]; volShIdx[tid]=volShIdx[tid+16];	} }
		if (BLOCK_SIZE_RED >=  16) { if( volShVal[tid]< volShVal[tid+8]) {
							 volShVal[tid]=volShVal[tid+8]; volShIdx[tid]=volShIdx[tid+8];	} }
		if (BLOCK_SIZE_RED >=   8) { if( volShVal[tid]< volShVal[tid+4]) {
							 volShVal[tid]=volShVal[tid+4]; volShIdx[tid]=volShIdx[tid+4];	} }
		if (BLOCK_SIZE_RED >=   4) { if( volShVal[tid]< volShVal[tid+2]) {
							 volShVal[tid]=volShVal[tid+2]; volShIdx[tid]=volShIdx[tid+2];	} }
		if (BLOCK_SIZE_RED >=   2) { if( volShVal[tid]< volShVal[tid+1]) {
							 volShVal[tid]=volShVal[tid+1]; volShIdx[tid]=volShIdx[tid+1];	} }
*/

		if( volShVal[tid]< volShVal[tid+32]) {
							 volShVal[tid]=volShVal[tid+32]; volShIdx[tid]=volShIdx[tid+32];	} 
		if( volShVal[tid]< volShVal[tid+16]) {
							 volShVal[tid]=volShVal[tid+16]; volShIdx[tid]=volShIdx[tid+16];	} 
		if( volShVal[tid]< volShVal[tid+8]) {
							 volShVal[tid]=volShVal[tid+8]; volShIdx[tid]=volShIdx[tid+8];	} 
		if( volShVal[tid]< volShVal[tid+4]) {
							 volShVal[tid]=volShVal[tid+4]; volShIdx[tid]=volShIdx[tid+4];	}
		if( volShVal[tid]< volShVal[tid+2]) {
							 volShVal[tid]=volShVal[tid+2]; volShIdx[tid]=volShIdx[tid+2];  }
		if( volShVal[tid]< volShVal[tid+1]) {
							 volShVal[tid]=volShVal[tid+1]; volShIdx[tid]=volShIdx[tid+1];	}
}

/*
  Do warp parallel reduction for minimum finding
*/
__device__ void minWarpReduce(volatile float *sdata,unsigned int tid)
{
		/*
		if (BLOCK_SIZE_RED >=  64) sdata[tid]=fminf(sdata[tid],sdata[tid+32]);
		if (BLOCK_SIZE_RED >=  32) sdata[tid]=fminf(sdata[tid],sdata[tid+16]);
		if (BLOCK_SIZE_RED >=  16) sdata[tid]=fminf(sdata[tid],sdata[tid+8]);
		if (BLOCK_SIZE_RED >=   8) sdata[tid]=fminf(sdata[tid],sdata[tid+4]);
		if (BLOCK_SIZE_RED >=   4) sdata[tid]=fminf(sdata[tid],sdata[tid+2]);
		if (BLOCK_SIZE_RED >=   2) sdata[tid]=fminf(sdata[tid],sdata[tid+1]);
		*/
		sdata[tid]=fminf(sdata[tid],sdata[tid+32]);
		sdata[tid]=fminf(sdata[tid],sdata[tid+16]);
		sdata[tid]=fminf(sdata[tid],sdata[tid+8]);
		sdata[tid]=fminf(sdata[tid],sdata[tid+4]);
		sdata[tid]=fminf(sdata[tid],sdata[tid+2]);
		sdata[tid]=fminf(sdata[tid],sdata[tid+1]);
}

/*
	Do parallel reduction for finding index "i" which maximize
	// i: Maximizes -y_i * grad(f)_i, i in I_up(\alpha)
*/
extern "C" __global__ void FindMaxIdx(const float* y, 
									  const float* alpha, 
									  const float* grad,
									  int * idxReduce, 
									  float* gradReduce,
									  const int N)
{

	__shared__ float shVals[BLOCK_SIZE_RED];     
	__shared__ int shIdx[BLOCK_SIZE_RED];
	
	// perform first level of reduction,
	// reading from global memory, writing to shared memory
	unsigned int tid = threadIdx.x;
	//unsigned int i = blockIdx.x*BLOCK_SIZE_RED*2 + threadIdx.x;
	//unsigned int gridSize = BLOCK_SIZE_RED*2*gridDim.x;

	unsigned int blockSize = blockDim.x;
	unsigned int i = blockIdx.x*blockDim.x*2 + threadIdx.x;
	unsigned int gridSize = blockDim.x*2*gridDim.x;
	
	shVals[tid]=-FLT_MAX;
	shIdx[tid]=-1;
   
	// we reduce multiple elements per thread.  The number is determined by the 
	// number of active thread blocks (via gridDim).  More blocks will result
	// in a larger gridSize and therefore fewer elements per thread
	float maxG=-FLT_MAX ;
	float tempMax=-FLT_MAX ;
	float yi=0;
	float alpha_i=0;
	
	while (i < N)
	{   
		yi=y[i];
		alpha_i=alpha[i];
		tempMax = (yi*alpha_i)<(yi==1?C:0) ? -(grad[i]*yi):-FLT_MAX;
		
		//if maxG==tempMax then tempMax is new max value, so remember its index, otherwise do nothing (return 0)
		//maxG==tempMax ? shIdx[tid]=i:0; 

		maxG<tempMax ? shIdx[tid]=i : 0;
		maxG = fmaxf(maxG, tempMax );
		
		// ensure we don't read out of bounds 
		if (i + blockSize < N) {
			yi=y[i + blockSize];
			alpha_i=alpha[i + blockSize];
			tempMax = (yi*alpha_i)<(yi==1?C:0) ? -(grad[i + blockSize]*yi):-FLT_MAX;
			
			//maxG = fmaxf(maxG, tempMax );
			//if maxG==tempMax then tempMax is new max value, so remember its index, otherwise do nothing (return 0)
			//maxG==tempMax ? shIdx[tid]=i+blockSize:0; 

			maxG<tempMax ? shIdx[tid]=i+blockSize : 0;
			maxG = fmaxf(maxG, tempMax );

		}
		i += gridSize;
	} 

	// each thread puts its local sum into shared memory 
	shVals[tid] = maxG;
	__syncthreads();

	

	// do reduction in shared mem
	/*
	if (BLOCK_SIZE_RED >= 512) { 
		if (tid < 256) { if( shVals[tid]< shVals[tid+256]) {
							 shVals[tid]=shVals[tid+256]; shIdx[tid]=shIdx[tid+256];	}} __syncthreads(); }
	if (BLOCK_SIZE_RED >= 256) { 
		if (tid < 128) { if( shVals[tid]< shVals[tid+128]) {
							 shVals[tid]=shVals[tid+128]; shIdx[tid]=shIdx[tid+128];	}} __syncthreads(); }
	if (BLOCK_SIZE_RED >= 128) { 
		if (tid < 64) { if( shVals[tid]< shVals[tid+64]) {
						 shVals[tid]=shVals[tid+64]; shIdx[tid]=shIdx[tid+64];	}} __syncthreads(); }
*/	
	
	if (blockSize >= 512) { 
		if (tid < 256) { if( shVals[tid]< shVals[tid+256]) {
							 shVals[tid]=shVals[tid+256]; shIdx[tid]=shIdx[tid+256];	}} __syncthreads(); }
	if (blockSize >= 256) { 
		if (tid < 128) { if( shVals[tid]< shVals[tid+128]) {
							 shVals[tid]=shVals[tid+128]; shIdx[tid]=shIdx[tid+128];	}} __syncthreads(); }
	if (blockSize >= 128) { 
		if (tid < 64) { if( shVals[tid]< shVals[tid+64]) {
							 shVals[tid]=shVals[tid+64]; shIdx[tid]=shIdx[tid+64];	}} __syncthreads(); }
	//gradReduce[tid] =shVals[tid];
	//idxReduce[tid] = shIdx[tid];
	//return;

	if (tid < 32)
	{
		// now that we are using warp-synchronous programming (below)
		// we need to declare our shared memory volatile so that the compiler
		// doesn't reorder stores to it and induce incorrect behavior.
		maxWarpReduce(shIdx,shVals,tid);
	}
	
	// write result for this block to global mem 
	if (tid == 0) {
		gradReduce[blockIdx.x] = shVals[0];
		idxReduce[blockIdx.x] = shIdx[0];
	}
	
}


/*
	Do parallel reduction for finding index "j" which minimaze
	j: mimimizes the decrease of obj value
    (if quadratic coefficeint <= 0, replace it with tau)
    -y_j*grad(f)_j < -y_i*grad(f)_i, j in I_low(\alpha)

y - 
alpha - 
grad  - 
Qi    - i-th column in kernel matrix, each value was mul by yi*yj
*/
extern "C" __global__ void FindMinIdx(const float * y,		//labels 
									  const float* alpha,   //alpha coef
									  const float* grad,	// gradient
									  const float* Qi,		// i-th column in kernel matrix
									  const float* QD,		// diagonal in kernel matris
									  int * idxReduce,		// array for results
									  float* gradReduce,	// array for results
									  float gradMax,
									  float QDi,
									  float Yi,
									  const int N)
{

	__shared__ float shVals[BLOCK_SIZE_RED];     
	__shared__ int shIdx[BLOCK_SIZE_RED];
	
	// perform first level of reduction,
	// reading from global memory, writing to shared memory
	unsigned int tid = threadIdx.x;
	//unsigned int i = blockIdx.x*BLOCK_SIZE_RED*2 + threadIdx.x;
	//unsigned int gridSize = BLOCK_SIZE_RED*2*gridDim.x;

	unsigned int blockSize = blockDim.x;
	unsigned int j = blockIdx.x*blockDim.x*2 + threadIdx.x;
	unsigned int gridSize = blockDim.x*2*gridDim.x;
	
	shVals[tid]=-FLT_MAX;
	shIdx[tid]=-1;
	// we reduce multiple elements per thread.  The number is determined by the 
	// number of active thread blocks (via gridDim).  More blocks will result
	// in a larger gridSize and therefore fewer elements per thread
	float maxG=-FLT_MAX ;
	float tempMax=-FLT_MAX ;
	float yj=0;
	float alpha_j=0;
	
	float quad_coef=0;
	float QD_i=QDi;
	float GMax=gradMax;
	float Y_i=Yi;
	while (j < N)
	{   
		yj=y[j];
		alpha_j=alpha[j];
		//in libsvm this formula is different for different value of yj, 
		//but when we mul by labels for i,j examples this formula can be computed as below
		quad_coef = (QD_i+QD[j]-2*Y_i*yj*Qi[j]);
		quad_coef = quad_coef< COEF_EPS ? COEF_EPS: quad_coef;

		//check if is not at lower bound
		tempMax = (yj*alpha_j)>(yj==1?0:-C) ? __fdividef(__powf(GMax+yj*grad[j],2.f),quad_coef):-FLT_MAX;
		
		//maxG = fmaxf(maxG, tempMax );
		//if maxG==tempMax then tempMax is new max value, so remember its index, otherwise do nothing (return 0)
		//maxG==tempMax ? shIdx[tid]=j:0; 
		//atomicMax, atomicCLA??
		maxG<tempMax ? shIdx[tid]=j : 0;
		maxG = fmaxf(maxG, tempMax );

		// ensure we don't read out of bounds 
		if (j + blockSize < N) {
			yj=y[j + blockSize];
			alpha_j=alpha[j + blockSize];
			quad_coef = (QD_i+QD[j+ blockSize]-2*Y_i*yj*Qi[j+ blockSize]);
			quad_coef = quad_coef< COEF_EPS ? COEF_EPS: quad_coef;

			tempMax = (yj*alpha_j)>(yj==1?0:-C) ? __fdividef(__powf(GMax+yj*grad[j+blockSize],2.f),quad_coef):-FLT_MAX;
			//maxG = fmaxf(maxG, tempMax );
			//if maxG==tempMax then tempMax is new max value, so remember its index, otherwise do nothing (return 0)
			//maxG==tempMax ? (shIdx[tid]=j+blockSize):0; 

			maxG<tempMax ? shIdx[tid]=j+blockSize : 0;
			maxG = fmaxf(maxG, tempMax );
		}
		j+= gridSize;
	} 

	// each thread puts its local sum into shared memory 
	shVals[tid] = maxG;
	__syncthreads();
/*
	gradReduce[tid] =shVals[tid];
		idxReduce[tid] = shIdx[tid];
	return;
*/
	// do reduction in shared mem
	
	if (blockSize >= 512) { 
		if (tid < 256) { if( shVals[tid]< shVals[tid+256]) {
							 shVals[tid]=shVals[tid+256]; shIdx[tid]=shIdx[tid+256];	}} __syncthreads(); }
	if (blockSize >= 256) { 
		if (tid < 128) { if( shVals[tid]< shVals[tid+128]) {
							 shVals[tid]=shVals[tid+128]; shIdx[tid]=shIdx[tid+128];	}} __syncthreads(); }
	if (blockSize >= 128) { 
		if (tid < 64) { if( shVals[tid]< shVals[tid+64]) {
							 shVals[tid]=shVals[tid+64]; shIdx[tid]=shIdx[tid+64];	}} __syncthreads(); }
	

	if (tid < 32)
	{
		// now that we are using warp-synchronous programming (below)
		// we need to declare our shared memory volatile so that the compiler
		// doesn't reorder stores to it and induce incorrect behavior.
		maxWarpReduce(shIdx,shVals,tid);
		
	}
	
	// write result for this block to global mem 
	if (tid == 0) {
		gradReduce[blockIdx.x] =shVals[0];
		idxReduce[blockIdx.x] = shIdx[0];
	}
	
}



/*
	Finds min Gradient value for stopping criterion
*/
extern "C" __global__ void FindStoppingGradVal(const float* y, 
									  const float* alpha, 
									  const float* grad,
									  float* gradReduce,
									  const int N)
{
	__shared__ float shVals[BLOCK_SIZE_RED];     
	// perform first level of reduction,
	// reading from global memory, writing to shared memory
	unsigned int tid = threadIdx.x;
	//unsigned int i = blockIdx.x*BLOCK_SIZE_RED*2 + threadIdx.x;
	//unsigned int gridSize = BLOCK_SIZE_RED*2*gridDim.x;

	unsigned int blockSize = blockDim.x;
	unsigned int j = blockIdx.x*blockDim.x*2 + threadIdx.x;
	unsigned int gridSize = blockDim.x*2*gridDim.x;
	
	shVals[tid]=FLT_MAX;
	float yj=0;
	float yj_far=0;
	// we reduce multiple elements per thread.  The number is determined by the 
	// number of active thread blocks (via gridDim).  More blocks will result
	// in a larger gridSize and therefore fewer elements per thread

	while (j < N)
	{   
		yj=y[j];
		yj_far = (j+blockSize)<N ? y[j+blockSize]:0;
		
		shVals[tid]=fminf(shVals[tid], fminf(
										(yj*alpha[j]) >(yj==1? 0:-C) ? -(grad[j]*yj): FLT_MAX,
										j+blockSize<N ?
										( ( yj_far*alpha[j+blockSize])>(yj_far==1? 0:-C) ? -(grad[j+blockSize]*yj_far): FLT_MAX)
										: FLT_MAX
										));
		
		j += gridSize;
	} 

	__syncthreads();


	// do reduction in shared mem
	if (blockSize >= 512) 
		if (tid < 256) { shVals[tid]=fminf(shVals[tid],shVals[tid+256]); __syncthreads(); }

	if (blockSize >= 256) 
		if (tid < 128) {  shVals[tid]=fminf(shVals[tid],shVals[tid+128]);  __syncthreads(); }

	if (blockSize >= 128)
		if (tid < 64) {  shVals[tid]=fminf(shVals[tid],shVals[tid+64]); __syncthreads(); }
	

	if (tid < 32)
	{
		// now that we are using warp-synchronous programming (below)
		// we need to declare our shared memory volatile so that the compiler
		// doesn't reorder stores to it and induce incorrect behavior.
		minWarpReduce(shVals,tid);
	}
	
	// write result for this block to global mem 
	if (tid == 0) {
		gradReduce[blockIdx.x] = shVals[0];
	}
	
}



/*

	Updates gradient 

	One threads process 4 gradients, inspired by Volkow http://www.cs.berkeley.edu/~volkov/volkov10-GTC.pdf
Qi - i-th kernel column
Qj - j-th kernel column
grad - gradient
diff_i  - alpha_i-old_alpha_i
diff_j  - alpha_j-old_alpha_j
N       - #rows
*/
extern "C" __global__ void UpdateGrad(const float* Qi, 
									  const float* Qj, 
									  float* grad,
									  float diff_i,
									  float diff_j,
									  const int N)
{
    int iblock = blockIdx.x; //+  gridDim.x*blockDim.x;
    int idx    = threadIdx.x+4*iblock*blockDim.x;
	//acumulators 
	float tempGrad[4];	
	float tempQi[4];	
	float tempQj[4];
	float alpha_i_diff=diff_i;
	float alpha_j_diff=diff_j;	
	//read 4 elements per thread int to register's
	for(int i=0;i<4;i++){
		/*
		tempGrad[i] = (idx+i*blockDim.x <N) ? grad[idx+i*blockDim.x]:0;
		tempQi[i]   = (idx+i*blockDim.x <N) ? Qi[idx+i*blockDim.x]:0;
		tempQj[i]   = (idx+i*blockDim.x <N) ? Qj[idx+i*blockDim.x]:0;
		*/
		if(idx+i*blockDim.x <N)
		{
			tempGrad[i] = grad[idx+i*blockDim.x]; 
			tempQi[i]=Qi[idx+i*blockDim.x]; 
			tempQj[i]=Qj[idx+i*blockDim.x];
		}
		//(idx+i*blockDim.x <N) ? (tempGrad[i] = grad[idx+i*blockDim.x]; tempQi[i]=Qi[idx+i*blockDim.x]; tempQj[i]=Qj[idx+i*blockDim.x]):0;
	}
	
	//do final computation
	for(int i=0;i<4;i++){
		(idx+i*blockDim.x <N) ? (grad[idx+i*blockDim.x]=tempGrad[i]+ alpha_i_diff*tempQi[i]+alpha_j_diff*tempQj[i]) :0;
		
	}
}