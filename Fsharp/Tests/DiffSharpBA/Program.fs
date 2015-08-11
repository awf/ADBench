﻿open System
open System.Diagnostics
open System.IO
//open DiffSharp.AD
open DiffSharp.AD.Specialized.Reverse1

////// IO //////

let N_CAM_PARAMS = 11
let ROT_IDX = 0
let CENTER_IDX = 3
let FOCAL_IDX = 6
let X0_IDX = 7
let RAD_IDX = 9

let read_ba_instance (fn:string) =
    let read_in_elements (fn:string) =
        let string_lines = File.ReadLines(fn)
        let separators = [|' '|]
        [| for line in string_lines do yield line.Split separators |] 
            |> Array.map (Array.filter (fun x -> x.Length > 0))

    let parse_double (arr:string[][]) =
        [|for elem in arr do yield (Array.map Double.Parse elem) |]
        
    let parse_int (arr:string[][]) =
        [|for elem in arr do yield (Array.map Int32.Parse elem) |]

    let data = read_in_elements fn

    let n = Int32.Parse data.[0].[0]
    let m = Int32.Parse data.[0].[1]
    let p = Int32.Parse data.[0].[2]
    
    let mutable offset = 1
    let cams = parse_double data.[offset..(offset+n-1)]

    offset <- offset + n
    let X = parse_double data.[offset..(offset+m-1)]
    
    offset <- offset + m
    let w = Array.map Double.Parse data.[offset] 
    
    offset <- offset + 1
    let obs = parse_int data.[offset..(offset+p-1)]

    offset <- offset + p
    let feat = parse_double data.[offset..(offset+p-1)]

    cams, X, w, obs, feat

////// Objective //////

let inline sqnorm (x:_[]) =
    x |> Array.map (fun x -> x*x) |> Array.sum

let inline dot_prod (x:_[]) (y:_[]) =
    Array.map2 (*) x y |> Array.sum

let inline sub_vec (x:_[]) (y:_[]) =
    Array.map2 (-) x y

let inline add_vec (x:_[]) (y:_[]) =
    Array.map2 (+) x y

let inline add_vec3 (x:_[]) (y:_[]) (z:_[]) =
    Array.map3 (fun a b c -> a+b+c) x y z

let inline mult_by_scalar (x:_[]) y =
    Array.map (fun a -> a*y) x

let rodrigues_rotate_point (rot:_[]) (X:_[]) =
    let theta = sqrt (sqnorm rot)
    let costheta = cos theta
    let sintheta = sin theta
    let theta_inv = 1. / theta

    let w = mult_by_scalar rot theta_inv
    let w_cross_X = [|w.[1]*X.[2] - w.[2]*X.[1]; w.[2]*X.[0] - w.[0]*X.[2]; w.[0]*X.[1] - w.[1]*X.[0];|]    
    let tmp = (dot_prod w X) * (1. - costheta)

    add_vec3 (mult_by_scalar X costheta) (mult_by_scalar w_cross_X sintheta) (mult_by_scalar w tmp)

let radial_distort (rad_params:_[]) (proj:_[]) =
    let rsq = sqnorm proj
    let L = 1. + rad_params.[0] * rsq + rad_params.[1] * rsq * rsq
    mult_by_scalar proj L

let project (cam:_[]) (X:_[]) =
    let Xcam = rodrigues_rotate_point cam.[ROT_IDX..(ROT_IDX+2)] (sub_vec X cam.[CENTER_IDX..(CENTER_IDX+2)])
    let distorted = radial_distort cam.[RAD_IDX..(RAD_IDX+1)] (mult_by_scalar Xcam.[0..1] (1./Xcam.[2]))
    add_vec cam.[X0_IDX..(X0_IDX+1)] (mult_by_scalar distorted cam.[FOCAL_IDX])

let compute_reproj_err (cam:_[]) (X:_[]) w (feat:_[]) =
    mult_by_scalar (sub_vec (project cam X) feat) w

let compute_f_prior_err f1 f2 f3 =
    f1 - 2.*f2 + f3

let compute_zach_weight_error w =
    1. - w*w

let ba_objective (cams:_[][]) (X:_[][]) (w:_[]) (obs:int[][]) (feat:float[][]) =
    let n = cams.Length
    let p = w.Length
    let reproj_err = 
        [|for i = 0 to p-1 do yield (compute_reproj_err cams.[obs.[i].[0]] X.[obs.[i].[1]] w.[i] feat.[i])|]
    let f_prior_err = [|for i = 0 to n-3 do yield (compute_f_prior_err cams.[i].[FOCAL_IDX] cams.[i].[FOCAL_IDX] cams.[i].[FOCAL_IDX])|]
    let w_err = Array.map compute_zach_weight_error w 
    reproj_err, f_prior_err, w_err

///// Derivative extras /////
let rodrigues_rotate_point_ (rot:D[]) (X:D[]) =
    let theta = sqrt (sqnorm rot)
    let costheta = cos theta
    let sintheta = sin theta
    let theta_inv = 1. / theta

    let w = mult_by_scalar rot theta_inv
    let w_cross_X = [|w.[1]*X.[2] - w.[2]*X.[1]; w.[2]*X.[0] - w.[0]*X.[2]; w.[0]*X.[1] - w.[1]*X.[0];|]    
    let tmp = (dot_prod w X) * (1. - costheta)

    add_vec3 (mult_by_scalar X costheta) (mult_by_scalar w_cross_X sintheta) (mult_by_scalar w tmp)

let radial_distort_ (rad_params:D[]) (proj:D[]) =
    let rsq = sqnorm proj
    let L = 1. + rad_params.[0] * rsq + rad_params.[1] * rsq * rsq
    mult_by_scalar proj L

let project_ (cam:D[]) (X:D[]) =
    let Xcam = rodrigues_rotate_point_ cam.[ROT_IDX..(ROT_IDX+2)] (sub_vec X cam.[CENTER_IDX..(CENTER_IDX+2)])
    let distorted = radial_distort_ cam.[RAD_IDX..(RAD_IDX+1)] (mult_by_scalar Xcam.[0..1] (1./Xcam.[2]))
    add_vec cam.[X0_IDX..(X0_IDX+1)] (mult_by_scalar distorted cam.[FOCAL_IDX])

let compute_reproj_err_ (cam:D[]) (X:D[]) (w:D) (feat:float[]) =
    mult_by_scalar (sub_vec (project_ cam X) feat) w
    
let compute_reproj_err_wrapper (parameters:_[]) (feat:float[]) =
    let X_off = N_CAM_PARAMS
    let w_off = X_off + 3
    compute_reproj_err_ parameters.[..(X_off-1)] parameters.[X_off..(X_off+2)] parameters.[w_off] feat

let compute_f_prior_err_ (fs:D[]) =
    fs.[0] - 2.*fs.[1] + fs.[2]

let compute_zach_weight_error_ (w:D) =
    1. - w*w
    
let vectorize (cam:_[]) (X:_[]) (w:_) =
    Array.append (Array.append cam X) [|w|]

//let ba_objective_ (cams:D[][]) (X:D[][]) (w:D[]) (obs:int[][]) (feat:float[][]) =
//    let n = cams.Length
//    let p = w.Length
//    let grad_compute_f_prior_err = grad' compute_f_prior_err_
//    let diff_w_err = diff' compute_zach_weight_error_
//    
//    let do_jac_reproj_err (parameters:_[]) (feat:float[]) =
//        let compute_reproj_err_wrapper_ (parameters:_[]) = 
//            compute_reproj_err_wrapper parameters feat
//        let jac_reproj_err = jacobian' compute_reproj_err_wrapper_
//        jac_reproj_err parameters
//
//    let J_reproj_err = 
//        [|for i = 0 to p-1 do 
//            yield (do_jac_reproj_err (vectorize cams.[obs.[i].[0]] X.[obs.[i].[1]] w.[i]) feat.[i])|]
//    let J_f_prior_err = 
//        [|for i = 0 to n-3 do 
//            yield (grad_compute_f_prior_err [|cams.[i].[FOCAL_IDX]; 
//                                                cams.[i].[FOCAL_IDX]; 
//                                                cams.[i].[FOCAL_IDX]|])|]
//    let J_w_err = Array.map diff_w_err w
//
//    J_reproj_err, J_f_prior_err, J_w_err
    
let ba_objective_ (cams:_[][]) (X:_[][]) (w:_[]) (obs:int[][]) (feat:float[][]) =
    let n = cams.Length
    let p = w.Length
    let grad_compute_f_prior_err = grad' compute_f_prior_err_
    let diff_w_err = diff' compute_zach_weight_error_
    
    let do_jac_reproj_err (parameters:_[]) (feat:float[]) =
        let compute_reproj_err_wrapper_ (parameters:_[]) = 
            compute_reproj_err_wrapper parameters feat
        let jac_reproj_err = jacobian' compute_reproj_err_wrapper_
        jac_reproj_err parameters

    let J_reproj_err = 
        [|for i = 0 to p-1 do 
            yield (do_jac_reproj_err (vectorize cams.[obs.[i].[0]] X.[obs.[i].[1]] w.[i]) feat.[i])|]
    let J_f_prior_err = 
        [|for i = 0 to n-3 do 
            yield (grad_compute_f_prior_err [|cams.[i].[FOCAL_IDX]; 
                                                cams.[i].[FOCAL_IDX]; 
                                                cams.[i].[FOCAL_IDX]|])|]
    let J_w_err = Array.map diff_w_err w

    J_reproj_err, J_f_prior_err, J_w_err

[<EntryPoint>]
let main argv = 
    let cams, X, w, obs, feat = read_ba_instance (argv.[0] + ".txt")

    let nruns = 1000

//    let reproj_err, f_prior_err, w_err = ba_objective cams X w obs feat
//    printfn "%A" reproj_err
//    printfn "%A" f_prior_err
//    printfn "%A" w_err
    let obj_stop_watch = Stopwatch.StartNew()
    for i = 1 to nruns do    
        ba_objective cams X w obs feat
    obj_stop_watch.Stop()
    
    let n = cams.Length
    let m = X.Length
    let p = obs.Length

//    let parameters = vectorize_in cams X w |> Array.map D
//    let err, J = jac_ba_objective parameters
//    printfn "%A" err
//    printfn "%A" J

//    let jac_stop_watch = Stopwatch.StartNew()
//    for i = 1 to nruns do
//        let camsD = cams |> Array.map (Array.map D)
//        let XD = X |> Array.map (Array.map D)
//        let wD = w |> Array.map D
//        ba_objective_ camsD XD wD obs feat
//    jac_stop_watch.Stop()
    
    let jac_stop_watch = Stopwatch.StartNew()
    for i = 1 to nruns do
        ba_objective_ cams X w obs feat
    jac_stop_watch.Stop()

    let tf = ((float obj_stop_watch.ElapsedMilliseconds) / 1000.) / (float nruns)
    let tJ = ((float jac_stop_watch.ElapsedMilliseconds) / 1000.) / (float nruns)
    printfn "tf: %f" tf
    printfn "tJ: %f" tJ
    printfn "tJ/tf: %f" (tJ/tf)
    
    0 