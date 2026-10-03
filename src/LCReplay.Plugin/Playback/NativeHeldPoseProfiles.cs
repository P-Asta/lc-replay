using System.Collections.Generic;
using UnityEngine;
namespace LCReplay.Plugin.Playback
{
    // v81 remote player poses after native RigBuilder evaluation. Menu playback
    // may not load the player prefab; these reusable defaults keep its IK arms.
    internal static class NativeHeldPoseProfiles
    {
        private static readonly Dictionary<string, Dictionary<string, Quaternion>> Poses = new Dictionary<string, Dictionary<string, Quaternion>>
        {
            ["HoldOneHandedItem"] = new Dictionary<string, Quaternion>
            {
                ["shoulder.R"] = new Quaternion(-0.470531821f, -0.479400158f, -0.51978904f, 0.527820647f),
                ["arm.R_upper"] = new Quaternion(-0.604105353f, -0.448455185f, -0.06882144f, 0.6551399f),
                ["arm.R_lower"] = new Quaternion(-0.78424114f, -0.49227798f, 0.03568176f, 0.3759722f),
                ["hand.R"] = new Quaternion(0.4555056f, 0.674923f, -0.09916085f, 0.571979761f),
                ["finger1.R"] = new Quaternion(-0.08828418f, 0.559496462f, -0.344161063f, 0.7488142f),
                ["finger1.R.001"] = new Quaternion(-0.295250028f, 0.06264083f, -0.325699538f, 0.89600414f),
                ["finger2.R"] = new Quaternion(-0.01750367f, 0.703416944f, -0.108838245f, 0.702177f),
                ["finger2.R.001"] = new Quaternion(-0.07399524f, -0.024447117f, -0.21990484f, 0.9724037f),
                ["finger3.R"] = new Quaternion(0.0331484973f, 0.6970654f, 0.00736146327f, 0.7162031f),
                ["finger3.R.001"] = new Quaternion(-0.05239592f, -0.01612237f, -0.298940957f, 0.9526957f),
                ["finger4.R"] = new Quaternion(0.0011612298f, 0.6969793f, 0.0181457661f, 0.7168607f),
                ["finger4.R.001"] = new Quaternion(-0.08184133f, -0.0185658783f, -0.3537974f, 0.9315496f),
                ["finger5.R"] = new Quaternion(-0.117793337f, 0.706377268f, -0.111354262f, 0.6890255f),
                ["finger5.R.001"] = new Quaternion(-0.114202671f, -0.01493199f, -0.327130973f, 0.937934f),
            },
            ["HoldLungApparatice"] = new Dictionary<string, Quaternion>
            {
                ["shoulder.L"] = new Quaternion(-0.4678165f, 0.4577881f, 0.522972f, 0.545965254f),
                ["arm.L_upper"] = new Quaternion(-0.5656335f, 0.791594267f, -0.230844125f, 0.0121820532f),
                ["arm.L_lower"] = new Quaternion(-0.3154445f, 0.103368983f, -0.34487164f, 0.8779939f),
                ["hand.L"] = new Quaternion(-0.43649742f, -0.395703584f, -0.2176537f, 0.77814883f),
                ["finger1.L"] = new Quaternion(0.130314857f, -0.539716959f, 0.39398706f, 0.732460141f),
                ["finger1.L.001"] = new Quaternion(-0.235137925f, 0.0803119838f, 0.442606777f, 0.8616028f),
                ["finger2.L"] = new Quaternion(0.15556632f, -0.6683572f, 0.0408271551f, 0.72624445f),
                ["finger2.L.001"] = new Quaternion(-0.0142977145f, -0.03482136f, 0.882026255f, 0.4696943f),
                ["finger3.L"] = new Quaternion(0.0527527742f, -0.691130042f, 0.03968317f, 0.7197094f),
                ["finger3.L.001"] = new Quaternion(-0.03386242f, -0.0239595119f, 0.9500325f, 0.309382617f),
                ["finger4.L"] = new Quaternion(-0.104769588f, -0.7156459f, -0.115116268f, 0.680898249f),
                ["finger4.L.001"] = new Quaternion(-0.0136571145f, -0.0536451042f, 0.9730202f, 0.223981157f),
                ["finger5.L"] = new Quaternion(-0.0503831953f, -0.7828495f, -0.08143596f, 0.614797831f),
                ["finger5.L.001"] = new Quaternion(0.0110170841f, 0.163734913f, 0.9219751f, 0.350758553f),
                ["shoulder.R"] = new Quaternion(-0.470531821f, -0.479400158f, -0.51978904f, 0.527820647f),
                ["arm.R_upper"] = new Quaternion(-0.582643569f, -0.230807126f, 0.29544f, 0.7210893f),
                ["arm.R_lower"] = new Quaternion(-0.5987499f, -0.3909966f, 0.2476443f, 0.6536762f),
                ["hand.R"] = new Quaternion(-0.249407917f, 0.270554423f, -0.633859336f, 0.6803076f),
                ["finger1.R"] = new Quaternion(-0.1453258f, 0.6318725f, -0.6077284f, 0.4585671f),
                ["finger1.R.001"] = new Quaternion(-0.0250033177f, 0.00362766441f, 0.0824991763f, 0.996270835f),
                ["finger2.R"] = new Quaternion(-0.386941075f, 0.4801811f, -0.002166554f, 0.7872091f),
                ["finger2.R.001"] = new Quaternion(-0.07513358f, -0.02418329f, -0.219932124f, 0.972316861f),
                ["finger3.R"] = new Quaternion(-0.397077978f, 0.5169425f, 0.233441561f, 0.7215293f),
                ["finger3.R.001"] = new Quaternion(-0.05239592f, -0.01612237f, -0.298940957f, 0.9526957f),
                ["finger4.R"] = new Quaternion(-0.430674464f, 0.515568256f, 0.1585348f, 0.723585248f),
                ["finger4.R.001"] = new Quaternion(-0.06355974f, -0.0268472582f, -0.0439895838f, 0.9966465f),
                ["finger5.R"] = new Quaternion(-0.38154307f, 0.5553928f, 0.2542416f, 0.6937759f),
                ["finger5.R.001"] = new Quaternion(-0.114811212f, 0.009143393f, -0.124931924f, 0.9854577f),
            },
            ["HoldItemBothHandsForward"] = new Dictionary<string, Quaternion>
            {
                ["shoulder.L"] = new Quaternion(-0.4678165f, 0.4577881f, 0.522972f, 0.545965254f),
                ["arm.L_upper"] = new Quaternion(-0.6108271f, 0.7037881f, -0.360100269f, -0.0435943864f),
                ["arm.L_lower"] = new Quaternion(-0.274141341f, 0.04027927f, -0.4630699f, 0.841896951f),
                ["hand.L"] = new Quaternion(0.148444876f, -0.8843955f, 0.105488196f, 0.429745257f),
                ["finger1.L"] = new Quaternion(0.130314857f, -0.539716959f, 0.39398706f, 0.732460141f),
                ["finger1.L.001"] = new Quaternion(-0.248416781f, -0.111088805f, 0.108629048f, 0.956110954f),
                ["finger2.L"] = new Quaternion(0.15556632f, -0.6683572f, 0.0408271551f, 0.72624445f),
                ["finger2.L.001"] = new Quaternion(-0.0157867875f, -0.09302426f, 0.50780654f, 0.856288433f),
                ["finger3.L"] = new Quaternion(0.07214041f, -0.6900267f, 0.0687088445f, 0.7168948f),
                ["finger3.L.001"] = new Quaternion(-0.0265885312f, -0.119731225f, 0.411206275f, 0.9032535f),
                ["finger4.L"] = new Quaternion(-0.006484222f, -0.732173443f, 0.0148738259f, 0.680925f),
                ["finger4.L.001"] = new Quaternion(0.0106976889f, -0.149463817f, 0.451719f, 0.8794863f),
                ["finger5.L"] = new Quaternion(0.0290218573f, -0.788856268f, 0.00610218663f, 0.613861859f),
                ["finger5.L.001"] = new Quaternion(-0.136321887f, -0.0515248626f, 0.425299376f, 0.8932424f),
                ["shoulder.R"] = new Quaternion(-0.470531821f, -0.479400158f, -0.51978904f, 0.527820647f),
                ["arm.R_upper"] = new Quaternion(-0.736351848f, -0.5087224f, -0.161764711f, 0.415715724f),
                ["arm.R_lower"] = new Quaternion(-0.490556747f, -0.5480936f, 0.04756907f, 0.675784469f),
                ["hand.R"] = new Quaternion(0.2950273f, 0.6698105f, -0.2561675f, 0.631419957f),
                ["finger1.R"] = new Quaternion(-0.1453258f, 0.6318725f, -0.6077284f, 0.4585671f),
                ["finger1.R.001"] = new Quaternion(-0.0250033177f, 0.00362766441f, 0.0824991763f, 0.996270835f),
                ["finger2.R"] = new Quaternion(-0.3766582f, 0.466327548f, 0.103767738f, 0.7936621f),
                ["finger2.R.001"] = new Quaternion(-0.07513358f, -0.02418329f, -0.219932124f, 0.972316861f),
                ["finger3.R"] = new Quaternion(-0.402793527f, 0.555085361f, 0.0609173961f, 0.725208044f),
                ["finger3.R.001"] = new Quaternion(-0.202977881f, -0.0250578821f, -0.264225155f, 0.942527f),
                ["finger4.R"] = new Quaternion(-0.43345046f, 0.5467631f, 0.0101563036f, 0.716287434f),
                ["finger4.R.001"] = new Quaternion(-0.122617744f, -0.08269029f, -0.14689669f, 0.978033066f),
                ["finger5.R"] = new Quaternion(-0.335432053f, 0.608056366f, 0.00194518245f, 0.7195479f),
                ["finger5.R.001"] = new Quaternion(-0.158206344f, -0.04378657f, -0.220835224f, 0.9613976f),
            },
        };
        internal static Dictionary<string, Quaternion>? Resolve(string clip) => Poses.TryGetValue(clip, out var pose) ? pose : null;
    }
}
